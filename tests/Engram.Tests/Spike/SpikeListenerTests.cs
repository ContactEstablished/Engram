using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Engram.Cli.Spike;

namespace Engram.Tests.Spike;

public sealed class SpikeListenerTests : IAsyncDisposable
{
    private const string Token = "tok_0123456789abcdefSECRET";
    private const string Body = """{"session_id":"s1","transcript_path":"/t","cwd":"/r","hook_event_name":"PostToolUse","tool_name":"Bash"}""";

    private readonly TempDir _temp = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly StringWriter _console = new();
    private SpikeListener? _listener;
    private Task? _loop;

    private string SpikeDir => _temp.Combine("spike");

    private int Start(string? expectedToken = Token)
    {
        _listener = new SpikeListener(SpikeDir, expectedToken, _console);
        _listener.Start(0);
        _loop = _listener.RunAsync(_cts.Token);
        return _listener.Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener?.Dispose();
        if (_loop is not null)
        {
            await _loop;
        }

        _cts.Dispose();
        _console.Dispose();
        _temp.Dispose();
    }

    private async Task<JsonElement> ReadSingleRecordAsync(string file)
    {
        var path = Path.Combine(SpikeDir, file);
        // The listener answers first and logs afterwards, so wait for a complete line. Share with the writer.
        var text = "";
        for (var i = 0; i < 100 && !text.EndsWith('\n'); i++)
        {
            if (File.Exists(path))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            }

            if (!text.EndsWith('\n'))
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1);
        return JsonDocument.Parse(lines[0]).RootElement.Clone();
    }

    [Fact]
    public async Task Logs_headers_and_body_answers_200_with_empty_body_and_masks_the_token()
    {
        var port = Start();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/hooks/tool")
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        request.Headers.TryAddWithoutValidation("X-Engram-Disable", "");
        request.Headers.TryAddWithoutValidation("X-Engram-Unlisted", "");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(200);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();

        var record = await ReadSingleRecordAsync("http.PostToolUse.jsonl");
        record.GetProperty("method").GetString().ShouldBe("POST");
        record.GetProperty("path").GetString().ShouldBe("/hooks/tool");
        record.GetProperty("payload").GetProperty("tool_name").GetString().ShouldBe("Bash");

        var auth = record.GetProperty("authorization");
        auth.GetProperty("matches_env").GetBoolean().ShouldBeTrue();
        auth.GetProperty("scheme").GetString().ShouldBe("Bearer");
        auth.GetProperty("token_length").GetInt32().ShouldBe(Token.Length);
        auth.GetProperty("looks_uninterpolated").GetBoolean().ShouldBeFalse();

        var headers = record.GetProperty("headers");
        headers.GetProperty("X-Engram-Unlisted").GetString().ShouldBe("");
        record.GetRawText().ShouldNotContain("SECRET", Case.Sensitive, "the token must never be written to disk");
        _console.ToString().ShouldContain("auth=match");
    }

    [Fact]
    public async Task Flags_a_wrong_or_uninterpolated_token()
    {
        var port = Start();

        await SendRawAsync(port, "POST /hooks/tool HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer $ENGRAM_TOKEN\r\nContent-Length: " +
            Encoding.UTF8.GetByteCount(Body) + "\r\n\r\n" + Body);

        var auth = (await ReadSingleRecordAsync("http.PostToolUse.jsonl")).GetProperty("authorization");
        auth.GetProperty("matches_env").GetBoolean().ShouldBeFalse();
        auth.GetProperty("looks_uninterpolated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Decodes_chunked_bodies()
    {
        var port = Start();
        var half = Body.Length / 2;
        var chunk1 = Body[..half];
        var chunk2 = Body[half..];

        var response = await SendRawAsync(port,
            "POST /hooks/tool HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n" +
            $"{chunk1.Length:x}\r\n{chunk1}\r\n{chunk2.Length:x};ext=1\r\n{chunk2}\r\n0\r\n\r\n");

        response.ShouldStartWith("HTTP/1.1 200 OK");
        var record = await ReadSingleRecordAsync("http.PostToolUse.jsonl");
        record.GetProperty("payload").GetProperty("hook_event_name").GetString().ShouldBe("PostToolUse");
    }

    [Fact]
    public async Task Non_json_body_is_logged_as_text_under_unknown()
    {
        var port = Start(expectedToken: null);

        await SendRawAsync(port, "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhello");

        var record = await ReadSingleRecordAsync("http.unknown.jsonl");
        record.GetProperty("body_text").GetString().ShouldBe("hello");
        record.GetProperty("authorization").GetProperty("present").GetBoolean().ShouldBeFalse();
        record.GetProperty("authorization").GetProperty("matches_env").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static async Task<string> SendRawAsync(int port, string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, TestContext.Current.CancellationToken);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
