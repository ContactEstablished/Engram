using System.Net;
using System.Text;
using Engram.Core;

namespace Engram.Tests.Worker;

public sealed class HookCaptureTests : IDisposable
{
    private readonly WorkerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Common(string session = "s1", string prompt = "p1", string cwd = "C:/work/app") =>
        $"\"session_id\":\"{session}\",\"prompt_id\":\"{prompt}\",\"transcript_path\":\"t.jsonl\",\"cwd\":\"{cwd}\"";

    private async Task<HttpResponseMessage> PostAsync(HttpClient client, string json, string path = "/hooks/event")
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri(path, UriKind.Relative), content, Ct);
    }

    private async Task SendAsync(params string[] payloads)
    {
        using var client = _factory.CreateHookClient();
        foreach (var payload in payloads)
        {
            using var response = await PostAsync(client, payload);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty("a 2xx with a text body is a hook error");
        }

        await _factory.DrainAsync();
    }

    [Fact]
    public async Task Rejects_missing_or_wrong_token()
    {
        using var anonymous = _factory.CreateClient();
        using var wrong = _factory.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new("Bearer", "nope");
        using var bare = _factory.CreateClient();
        bare.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer");

        foreach (var client in new[] { anonymous, wrong, bare })
        {
            using var response = await PostAsync(client, "{}");
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Rejects_browser_origin_and_foreign_host()
    {
        using var withOrigin = _factory.CreateHookClient();
        withOrigin.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        using var foreignHost = _factory.CreateHookClient();
        foreignHost.DefaultRequestHeaders.Host = "evil.example";

        (await PostAsync(withOrigin, "{}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(foreignHost, "{}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Recursion_guard_header_drops_the_event()
    {
        using var client = _factory.CreateHookClient();
        client.DefaultRequestHeaders.Add("X-Engram-Disable", "1");

        using var response = await PostAsync(client, $$$"""{"hook_event_name":"UserPromptSubmit",{{{Common()}}},"prompt":"hi"}""");
        await _factory.DrainAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.Count("SELECT count(*) FROM prompts").ShouldBe(0);
    }

    [Fact]
    public async Task Garbage_bodies_are_accepted_and_ignored()
    {
        await SendAsync("not json", "{}", """{"hook_event_name":"Notification","session_id":"x","transcript_path":"t","cwd":"c"}""");

        _factory.Count("SELECT count(*) FROM sessions").ShouldBe(0);
    }

    [Fact]
    public async Task A_turn_is_stored_with_prompt_tool_events_and_an_observer_job()
    {
        await SendAsync(
            $$$"""{"hook_event_name":"SessionStart",{{{Common()}}},"source":"startup","model":"claude-opus-5-5"}""",
            $$$"""{"hook_event_name":"UserPromptSubmit",{{{Common()}}},"prompt":"fix the bug"}""",
            $$$"""{"hook_event_name":"PostToolUse",{{{Common()}}},"tool_name":"Bash","tool_use_id":"t1","tool_input":{"command":"dotnet test"},"tool_response":{"stdout":"ok","stderr":""},"duration_ms":1234.4}""",
            $$$"""{"hook_event_name":"PostToolUseFailure",{{{Common()}}},"tool_name":"Bash","tool_use_id":"t2","tool_input":{"command":"exit 3"},"error":"Exit code 3","is_interrupt":false}""",
            $$$"""{"hook_event_name":"Stop",{{{Common()}}},"stop_hook_active":false,"last_assistant_message":"Fixed it."}""",
            $$$"""{"hook_event_name":"SessionEnd",{{{Common()}}},"reason":"prompt_input_exit"}""");

        var session = _factory.Query<(string Source, string Model, string Platform, string EndReason)>(
            "SELECT source, model, platform, end_reason FROM sessions WHERE id = 's1'").ShouldHaveSingleItem();
        session.Source.ShouldBe("startup");
        session.Model.ShouldBe("claude-opus-5-5");
        session.EndReason.ShouldBe("prompt_input_exit");
        _factory.Query<string>("SELECT text FROM prompts WHERE prompt_id = 'p1'").ShouldBe(["fix the bug"]);
        _factory.Query<(string HookEvent, string? ToolName, long IsError, long? DurationMs)>(
                "SELECT hook_event, tool_name, is_error, duration_ms FROM events ORDER BY id")
            .ShouldBe([("PostToolUse", "Bash", 0, 1234), ("PostToolUseFailure", "Bash", 1, null), ("Stop", null, 0, null)]);
        _factory.Query<string>("SELECT text FROM events WHERE hook_event = 'Stop'").ShouldBe(["Fixed it."]);
        _factory.Query<(string Kind, string? PromptId)>("SELECT kind, prompt_id FROM jobs ORDER BY id")
            .ShouldBe([("observe-turn", "p1"), ("summarize-session", null)]);
        _factory.Query<string>("SELECT identity FROM projects").ShouldHaveSingleItem().ShouldStartWith("dir:");
    }

    [Fact]
    public async Task Retried_hooks_are_stored_once()
    {
        var tool = $$$"""{"hook_event_name":"PostToolUse",{{{Common()}}},"tool_name":"Bash","tool_use_id":"t1","tool_input":{"command":"ls"},"tool_response":{"stdout":""}}""";
        var stop = $$$"""{"hook_event_name":"Stop",{{{Common()}}},"stop_hook_active":false}""";

        await SendAsync(tool, tool, stop, stop);

        _factory.Count("SELECT count(*) FROM events WHERE hook_event = 'PostToolUse'").ShouldBe(1);
        _factory.Count("SELECT count(*) FROM jobs").ShouldBe(1);
    }

    [Fact]
    public async Task Secrets_are_redacted_whole_files_dropped_and_long_strings_truncated()
    {
        var longOutput = new string('x', 10_000);
        await SendAsync(
            $$$"""{"hook_event_name":"UserPromptSubmit",{{{Common()}}},"prompt":"use key sk-ant-api03-abcdefghijklmnop <private>my salary</private> ok"}""",
            $$$"""{"hook_event_name":"PostToolUse",{{{Common()}}},"tool_name":"Bash","tool_use_id":"t1","tool_input":{"command":"curl -H 'Authorization: Bearer abcdefghijklmnopqrstu' x"},"tool_response":{"stdout":"{{{longOutput}}}"}}""",
            $$$"""{"hook_event_name":"PostToolUse",{{{Common()}}},"tool_name":"Edit","tool_use_id":"t2","tool_input":{"file_path":"C:/work/app/a.cs","old_string":"a","new_string":"b"},"tool_response":{"filePath":"C:/work/app/a.cs","originalFile":"WHOLE FILE","structuredPatch":[]}}""");

        var prompt = _factory.Query<string>("SELECT text FROM prompts").ShouldHaveSingleItem();
        prompt.ShouldBe("use key <REDACTED> <private-removed> ok");

        var bash = _factory.Query<(string Input, string Response)>("SELECT tool_input, tool_response FROM events WHERE tool_use_id = 't1'").ShouldHaveSingleItem();
        bash.Input.ShouldContain("Bearer <REDACTED>");
        bash.Response.Length.ShouldBeLessThan(4300);
        bash.Response.ShouldContain("…<truncated 5904 chars>");

        var edit = _factory.Query<string>("SELECT tool_response FROM events WHERE tool_use_id = 't2'").ShouldHaveSingleItem();
        edit.ShouldNotContain("WHOLE FILE");
        edit.ShouldContain("\"originalFile\":\"<omitted>\"");
    }

    [Fact]
    public async Task Filtered_tools_subagent_handbacks_excluded_paths_and_observer_runs_are_not_stored()
    {
        var sandbox = Path.Combine(EngramPaths.ObserverSandbox(_factory.Home), "run1").Replace('\\', '/');
        await SendAsync(
            $$$"""{"hook_event_name":"PostToolUse",{{{Common()}}},"tool_name":"Read","tool_use_id":"r1","tool_input":{"file_path":"a.cs"},"tool_response":{}}""",
            $$$"""{"hook_event_name":"PostToolUse",{{{Common()}}},"tool_name":"ToolSearch","tool_use_id":"r2","tool_input":{"query":"x"},"tool_response":{}}""",
            $$$"""{"hook_event_name":"PostToolUse",{{{Common()}}},"tool_name":"SubagentHandback","tool_use_id":"r3","tool_input":{"message":"x"},"tool_response":{}}""",
            $$$"""{"hook_event_name":"PostToolUse",{{{Common()}}},"tool_name":"Write","tool_use_id":"r4","tool_input":{"file_path":"C:/work/app/.env.local","content":"A=1"},"tool_response":{}}""",
            $$$"""{"hook_event_name":"PostToolUse",{{{Common()}}},"tool_name":"Edit","tool_use_id":"r5","tool_input":{"file_path":"config/secrets.prod.json"},"tool_response":{}}""",
            $$$"""{"hook_event_name":"UserPromptSubmit",{{{Common()}}},"prompt":"<agent-message from=\"a1\">\n[Subagent hand-back] report"}""",
            $$$"""{"hook_event_name":"UserPromptSubmit",{{{Common(session: "obs", cwd: sandbox)}}},"prompt":"summarize"}""");

        _factory.Count("SELECT count(*) FROM events").ShouldBe(0);
        _factory.Count("SELECT count(*) FROM prompts").ShouldBe(0);
        _factory.Count("SELECT count(*) FROM sessions WHERE id = 'obs'").ShouldBe(0);
    }

    [Fact]
    public async Task Spooled_payloads_are_ingested()
    {
        var spool = EngramPaths.Spool(_factory.Home);
        Directory.CreateDirectory(spool);
        File.WriteAllText(Path.Combine(spool, "0001.json"), $$$"""{"hook_event_name":"SessionStart",{{{Common("spooled")}}},"source":"startup"}""");
        using var client = _factory.CreateClient(); // starts the host

        var ingestor = _factory.Services.GetService(typeof(Engram.Worker.Capture.SpoolIngestor)) as Engram.Worker.Capture.SpoolIngestor;
        ingestor.ShouldNotBeNull().IngestOnce();
        await _factory.DrainAsync();

        _factory.Count("SELECT count(*) FROM sessions WHERE id = 'spooled'").ShouldBe(1);
        Directory.GetFiles(spool).ShouldBeEmpty();
    }
}
