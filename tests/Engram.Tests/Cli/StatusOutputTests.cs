using Engram.Cli.Commands;

namespace Engram.Tests.Cli;

public sealed class StatusOutputTests
{
    [Fact]
    public void Stats_are_printed_as_readable_lines()
    {
        var output = new StringWriter();

        StatusCommand.PrintStats("""
            {"counts":{"sessions":3,"prompts":12,"events":40,"observations":7,"summaries":2},
             "jobs":{"pending":1,"running":0,"done":14,"parked":1,"last_error":"claude timed out after 120s"},
             "observer":{"provider":"claude-cli","model":"haiku","max_calls_per_hour":120,"calls_24h":14,"failed_24h":2,"input_tokens_24h":9800,"output_tokens_24h":1200,"cost_usd_24h":0.021}}
            """, output);

        var text = output.ToString();
        text.ShouldContain("3 sessions, 12 prompts, 40 tool events, 7 observations, 2 summaries");
        text.ShouldContain("1 pending, 0 running, 14 done, 1 parked");
        text.ShouldContain("last error:    claude timed out after 120s");
        text.ShouldContain("claude-cli/haiku, last 24h: 14 calls (2 failed), 9800 in / 1200 out tokens, list-price cost ~$0.0210; budget 120/hour");
    }

    [Fact]
    public void An_observer_that_is_off_says_so()
    {
        var output = new StringWriter();

        StatusCommand.PrintStats("""{"counts":{},"jobs":{"pending":4},"observer":{"provider":"none"}}""", output);

        output.ToString().ShouldContain("observer:      off");
    }

    [Fact]
    public void Recent_prints_summaries_then_observations_and_handles_an_empty_database()
    {
        var output = new StringWriter();
        RecentCommand.Print("""
            {"observations":[{"id":7,"type":"gotcha","title":"Rebuild node-pty","narrative":"It breaks after Electron bumps.","facts":"[]","files":"[\"package.json\"]","branch":"main","created_at":"2026-11-26T12:00:00.0000000Z","project":"chorus"}],
             "summaries":[{"session_id":"s","kind":"final","request":"upgrade electron","investigated":"","learned":"needs rebuild","completed":"rebuilt","next_steps":"","branch":"main","created_at":"2026-11-26T12:05:00Z","project":"chorus"}]}
            """, output);
        var empty = new StringWriter();
        RecentCommand.Print("""{"observations":[],"summaries":[]}""", empty);

        var text = output.ToString();
        text.ShouldContain("SESSION SUMMARIES");
        text.ShouldContain("request: upgrade electron");
        text.ShouldNotContain("investigated:", Case.Sensitive, "empty fields are skipped");
        text.ShouldContain("#7 gotcha");
        text.ShouldContain("Rebuild node-pty");
        text.ShouldContain("files: [\"package.json\"]");
        empty.ToString().ShouldContain("Nothing yet");
    }

    [Theory]
    [InlineData(new string[0], 10)]
    [InlineData(new[] { "--limit", "25" }, 25)]
    [InlineData(new[] { "-n", "500" }, 100)]
    public void Recent_parses_its_limit(string[] args, int expected) => RecentCommand.Parse(args).ShouldBe(expected);

    [Fact]
    public void Recent_rejects_unknown_options() => Should.Throw<ArgumentException>(() => RecentCommand.Parse(["--bogus"]));
}

public sealed class WorkerClientTests
{
    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Plain_responses_are_split_into_status_and_body()
    {
        var (status, body) = Engram.Cli.Worker.WorkerClient.ParseResponse(Bytes("HTTP/1.1 200 OK\r\nContent-Length: 15\r\n\r\n{\"status\":\"ok\"}"));

        status.ShouldBe(200);
        body.ShouldBe("{\"status\":\"ok\"}");
    }

    [Fact]
    public void Chunked_bodies_are_decoded_even_when_a_chunk_splits_a_multibyte_character()
    {
        // Kestrel answers Results.Json(...) this way: no Content-Length, hex chunk sizes. "é" is two bytes (C3 A9).
        var raw = new List<byte>();
        raw.AddRange(Bytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: application/json\r\n\r\n"));
        raw.AddRange(Bytes("7\r\n{\"a\":\""));  // 6 bytes of JSON ...
        raw.Add(0xC3);                           // ... plus the first byte of é
        raw.AddRange(Bytes("\r\n5\r\n"));
        raw.Add(0xA9);                           // the second byte of é
        raw.AddRange(Bytes("bc\"}\r\n0\r\n\r\n"));

        var (status, body) = Engram.Cli.Worker.WorkerClient.ParseResponse([.. raw]);

        status.ShouldBe(200);
        body.ShouldBe("{\"a\":\"ébc\"}");
    }

    [Fact]
    public void A_header_only_or_garbage_response_does_not_throw()
    {
        Engram.Cli.Worker.WorkerClient.ParseResponse(Bytes("HTTP/1.1 204 No Content")).ShouldBe((204, string.Empty));
        Engram.Cli.Worker.WorkerClient.ParseResponse(Bytes("garbage")).ShouldBe((null, null));
        Engram.Cli.Worker.WorkerClient.ParseResponse([]).ShouldBe((null, null));
    }
}
