using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Engram.Core.Hooks;

namespace Engram.Cli.Spike;

/// <summary>
/// Minimal loopback HTTP/1.1 listener for the Phase 0 HTTP-hook check. Logs each request's headers and
/// body to <c>http.&lt;event&gt;.jsonl</c> and always answers <c>200</c> with an empty body (a 2xx with a
/// text body counts as a hook error in Claude Code).
/// Built on <see cref="TcpListener"/> so it binds 127.0.0.1 without an http.sys URL ACL and keeps
/// Kestrel out of the AOT CLI.
/// </summary>
internal sealed class SpikeListener : IDisposable
{
    internal const int MaxHeaderBytes = 64 * 1024;
    internal const int MaxBodyBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] s_okResponse = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
    private static readonly string[] s_sensitiveHeaders = ["proxy-authorization", "cookie", "x-api-key"];

    private static readonly JsonWriterOptions s_writerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _spikeDir;
    private readonly string? _expectedToken;
    private readonly TextWriter _console;
    private TcpListener? _tcp;

    public SpikeListener(string spikeDir, string? expectedToken, TextWriter console)
    {
        _spikeDir = spikeDir;
        _expectedToken = string.IsNullOrEmpty(expectedToken) ? null : expectedToken;
        _console = console;
    }

    public int Port { get; private set; }

    /// <summary>Binds 127.0.0.1:<paramref name="port"/> (0 = any free port).</summary>
    public void Start(int port)
    {
        _tcp = new TcpListener(IPAddress.Loopback, port);
        _tcp.Start();
        Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var tcp = _tcp ?? throw new InvalidOperationException("Call Start first.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C
        }
    }

    public void Dispose() => _tcp?.Stop();

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_requestTimeout);
        var receivedAt = DateTimeOffset.Now;
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";

        try
        {
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(s_okResponse, timeout.Token).ConfigureAwait(false);
            Log(request, receivedAt, remote);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or FormatException or InvalidDataException)
        {
            SafeFile.TryLogError(_spikeDir, $"listen: {remote}: {ex.GetType().Name}: {ex.Message}");
            WriteConsole($"{receivedAt:HH:mm:ss} ERROR {remote}: {ex.Message}");
        }
    }

    internal sealed record HttpRequest(string Method, string Path, string Version, List<KeyValuePair<string, string>> Headers, byte[] Body)
    {
        public string? Header(string name) =>
            Headers.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();
    }

    internal static async Task<HttpRequest> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var reader = new BufferedReader(stream);

        var headerBlock = await reader.ReadUntilAsync("\r\n\r\n"u8.ToArray(), MaxHeaderBytes, ct).ConfigureAwait(false);
        var lines = Encoding.Latin1.GetString(headerBlock).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3)
        {
            throw new FormatException($"Bad request line: '{lines[0]}'");
        }

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new FormatException($"Bad header line: '{line}'");
            }

            headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        var request = new HttpRequest(requestLine[0], requestLine[1], requestLine[2], headers, []);

        if (string.Equals(request.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        }

        byte[] body;
        if (request.Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
        {
            body = await reader.ReadChunkedAsync(MaxBodyBytes, ct).ConfigureAwait(false);
        }
        else if (request.Header("Content-Length") is { } lengthText)
        {
            var length = int.Parse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture);
            if (length > MaxBodyBytes)
            {
                throw new InvalidDataException($"Body too large ({length} bytes).");
            }

            body = await reader.ReadExactlyAsync(length, ct).ConfigureAwait(false);
        }
        else
        {
            body = [];
        }

        return request with { Body = body };
    }

    private void Log(HttpRequest request, DateTimeOffset receivedAt, string remote)
    {
        string? eventName = null;
        JsonDocument? doc = null;
        try
        {
            if (request.Body.Length > 0)
            {
                try
                {
                    doc = JsonDocument.Parse(request.Body);
                    eventName = HookPayloadReader.PeekEventName(request.Body);
                }
                catch (JsonException)
                {
                    doc = null;
                }
            }

            var auth = Inspect(request.Header("Authorization"));
            var buffer = new ArrayBufferWriter<byte>(request.Body.Length + 1024);
            using (var w = new Utf8JsonWriter(buffer, s_writerOptions))
            {
                w.WriteStartObject();
                w.WriteString("received_at", receivedAt);
                w.WriteString("remote", remote);
                w.WriteString("method", request.Method);
                w.WriteString("path", request.Path);
                w.WriteString("http_version", request.Version);

                w.WriteStartObject("headers");
                foreach (var group in request.Headers.GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var name = group.First().Key; // keep the casing the client sent
                    var value = string.Join(", ", group.Select(h => h.Value));
                    if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        value = auth.Masked;
                    }
                    else if (s_sensitiveHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        value = $"<masked, {value.Length} chars>";
                    }

                    w.WriteString(name, value);
                }

                w.WriteEndObject();

                w.WriteStartObject("authorization");
                w.WriteBoolean("present", auth.Present);
                w.WriteString("scheme", auth.Scheme);
                w.WriteString("masked", auth.Masked);
                w.WriteNumber("token_length", auth.TokenLength);
                w.WriteBoolean("looks_uninterpolated", auth.LooksUninterpolated);
                if (auth.MatchesEnv is { } matches)
                {
                    w.WriteBoolean("matches_env", matches);
                }
                else
                {
                    w.WriteNull("matches_env"); // listener has no ENGRAM_TOKEN to compare against
                }

                w.WriteEndObject();

                w.WriteNumber("body_bytes", request.Body.Length);
                if (doc is not null)
                {
                    w.WritePropertyName("payload");
                    doc.RootElement.WriteTo(w);
                }
                else if (request.Body.Length > 0)
                {
                    w.WriteString("body_text", Encoding.UTF8.GetString(request.Body));
                }

                w.WriteEndObject();
            }

            buffer.Write("\n"u8);
            var file = Path.Combine(_spikeDir, "http." + SpikeCapture.FileNameFor(eventName) + ".jsonl");
            SafeFile.Append(file, buffer.WrittenSpan);

            var authText = !auth.Present ? "missing" : auth.MatchesEnv switch { true => "match", false => "MISMATCH", null => "present(no ENGRAM_TOKEN here)" };
            WriteConsole($"{receivedAt:HH:mm:ss} {request.Method} {request.Path} event={eventName ?? "-"} bytes={request.Body.Length} auth={authText} " +
                $"x-engram-disable='{request.Header("X-Engram-Disable")}' x-engram-unlisted='{request.Header("X-Engram-Unlisted")}'");
        }
        catch (Exception ex)
        {
            SafeFile.TryLogError(_spikeDir, $"listen: log failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            doc?.Dispose();
        }
    }

    internal readonly record struct AuthInfo(bool Present, string? Scheme, string Masked, int TokenLength, bool LooksUninterpolated, bool? MatchesEnv);

    internal AuthInfo Inspect(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return new(false, null, "", 0, false, null);
        }

        var space = header.IndexOf(' ', StringComparison.Ordinal);
        var scheme = space > 0 ? header[..space] : null;
        var token = space > 0 ? header[(space + 1)..].Trim() : header;
        var masked = token.Length >= 12 ? $"{scheme} {token[..4]}…({token.Length} chars)" : $"{scheme} <{token.Length} chars>";
        bool? matches = _expectedToken is null
            ? null
            : CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_expectedToken));
        return new(true, scheme, masked.Trim(), token.Length, token.Contains('$', StringComparison.Ordinal), matches);
    }

    private void WriteConsole(string line)
    {
        lock (_console)
        {
            _console.WriteLine(line);
            _console.Flush();
        }
    }

    /// <summary>Reads delimited and length-prefixed data from a stream with one shared buffer.</summary>
    private sealed class BufferedReader(Stream stream)
    {
        private byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        public async Task<byte[]> ReadUntilAsync(byte[] delimiter, int max, CancellationToken ct)
        {
            while (true)
            {
                var index = _buffer.AsSpan(_start, _end - _start).IndexOf(delimiter);
                if (index >= 0)
                {
                    var result = _buffer.AsSpan(_start, index).ToArray();
                    _start += index + delimiter.Length;
                    return result;
                }

                if (_end - _start > max)
                {
                    throw new InvalidDataException("Header block too large.");
                }

                if (!await FillAsync(ct).ConfigureAwait(false))
                {
                    throw new IOException("Connection closed before the request was complete.");
                }
            }
        }

        public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
        {
            while (_end - _start < count)
            {
                if (!await FillAsync(ct).ConfigureAwait(false))
                {
                    throw new IOException("Connection closed before the body was complete.");
                }
            }

            var result = _buffer.AsSpan(_start, count).ToArray();
            _start += count;
            return result;
        }

        public async Task<byte[]> ReadChunkedAsync(int max, CancellationToken ct)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var sizeLine = Encoding.ASCII.GetString(await ReadUntilAsync("\r\n"u8.ToArray(), 1024, ct).ConfigureAwait(false));
                var semicolon = sizeLine.IndexOf(';', StringComparison.Ordinal);
                var size = int.Parse(semicolon >= 0 ? sizeLine[..semicolon] : sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    // Skip trailers up to the blank line.
                    while ((await ReadUntilAsync("\r\n"u8.ToArray(), MaxHeaderBytes, ct).ConfigureAwait(false)).Length > 0)
                    {
                    }

                    return body.ToArray();
                }

                if (body.Length + size > max)
                {
                    throw new InvalidDataException("Chunked body too large.");
                }

                body.Write(await ReadExactlyAsync(size, ct).ConfigureAwait(false));
                await ReadExactlyAsync(2, ct).ConfigureAwait(false); // CRLF after each chunk
            }
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
            _end += read;
            return read > 0;
        }
    }
}
