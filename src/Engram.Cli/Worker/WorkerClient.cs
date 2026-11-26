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
    public static int? Post(int port, string path, string? token, ReadOnlySpan<byte> body, TimeSpan timeout, IReadOnlyDictionary<string, string>? headers = null)
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
        return Send(port, request, timeout).Status;
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

            var text = Encoding.UTF8.GetString(response.GetBuffer(), 0, (int)response.Length);
            var parts = text.Split(' ', 3);
            if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            {
                return (null, null);
            }

            var bodyStart = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return (status, bodyStart >= 0 ? text[(bodyStart + 4)..] : string.Empty);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            return (null, null);
        }
    }
}
