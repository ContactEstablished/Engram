using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Engram.Cli.Worker;

/// <summary>
/// A tiny HTTP/1.1 client for the local worker. Raw sockets instead of HttpClient keep the AOT binary
/// small and the cold start short; the worker is always plain HTTP on loopback.
/// </summary>
internal static class WorkerClient
{
    /// <summary>POSTs <paramref name="body"/>; returns the status code, or null if the worker didn't answer in time.</summary>
    public static int? Post(int port, string path, string? token, ReadOnlySpan<byte> body, TimeSpan timeout, IReadOnlyDictionary<string, string>? headers = null) =>
        PostForBody(port, path, token, body, timeout, headers).Status;

    /// <summary>POSTs <paramref name="body"/>; returns status and response body, or (null, null) if unreachable.</summary>
    public static (int? Status, string? Body) PostForBody(int port, string path, string? token, ReadOnlySpan<byte> body, TimeSpan timeout, IReadOnlyDictionary<string, string>? headers = null)
    {
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"POST {path} HTTP/1.1\r\n")
            .Append(CultureInfo.InvariantCulture, $"Host: 127.0.0.1:{port}\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n")
            .Append("Connection: close\r\n");
        if (token is not null)
        {
            head.Append(CultureInfo.InvariantCulture, $"Authorization: Bearer {token}\r\n");
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }

        head.Append("\r\n");
        var request = new byte[Encoding.ASCII.GetByteCount(head.ToString()) + body.Length];
        var written = Encoding.ASCII.GetBytes(head.ToString(), request);
        body.CopyTo(request.AsSpan(written));
        return Send(port, request, timeout);
    }

    /// <summary>GETs <paramref name="path"/>; returns status and body, or (null, null) if unreachable.</summary>
    public static (int? Status, string? Body) Get(int port, string path, TimeSpan timeout, string? token = null)
    {
        var auth = token is null ? string.Empty : $"Authorization: Bearer {token}\r\n";
        var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{auth}Connection: close\r\n\r\n");
        return Send(port, request, timeout);
    }

    private static (int? Status, string? Body) Send(int port, byte[] request, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cts.Token).AsTask().GetAwaiter().GetResult();
            var sent = 0;
            while (sent < request.Length)
            {
                sent += socket.SendAsync(request.AsMemory(sent), SocketFlags.None, cts.Token).AsTask().GetAwaiter().GetResult();
            }

            // Connection: close, so read to EOF (responses are tiny).
            using var response = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token).AsTask().GetAwaiter().GetResult()) > 0)
            {
                response.Write(buffer, 0, read);
                if (response.Length > 1024 * 1024)
                {
                    break;
                }
            }

            return ParseResponse(response.GetBuffer().AsSpan(0, (int)response.Length));
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            return (null, null);
        }
    }

    /// <summary>Splits an HTTP/1.1 response into status and body, decoding <c>Transfer-Encoding: chunked</c>.</summary>
    internal static (int? Status, string? Body) ParseResponse(ReadOnlySpan<byte> raw)
    {
        var headEnd = raw.IndexOf("\r\n\r\n"u8);
        var head = Encoding.ASCII.GetString(headEnd >= 0 ? raw[..headEnd] : raw);
        var statusLine = head.Split("\r\n", 2)[0].Split(' ', 3);
        if (statusLine.Length < 2 || !int.TryParse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
        {
            return (null, null);
        }

        if (headEnd < 0)
        {
            return (status, string.Empty);
        }

        var body = raw[(headEnd + 4)..];
        var chunked = head.Contains("transfer-encoding: chunked", StringComparison.OrdinalIgnoreCase);
        return (status, Encoding.UTF8.GetString(chunked ? DecodeChunked(body) : body));
    }

    private static byte[] DecodeChunked(ReadOnlySpan<byte> body)
    {
        var result = new List<byte>(body.Length);
        while (!body.IsEmpty)
        {
            var lineEnd = body.IndexOf("\r\n"u8);
            if (lineEnd < 0)
            {
                break;
            }

            var sizeText = Encoding.ASCII.GetString(body[..lineEnd]);
            var semicolon = sizeText.IndexOf(';', StringComparison.Ordinal);
            if (!int.TryParse(semicolon >= 0 ? sizeText[..semicolon] : sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size == 0)
            {
                break;
            }

            body = body[(lineEnd + 2)..];
            var take = Math.Min(size, body.Length);
            result.AddRange(body[..take].ToArray());
            body = body[take..];
            body = body.Length >= 2 ? body[2..] : [];
        }

        return [.. result];
    }
}
