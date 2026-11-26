using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Engram.Tests.Worker;

namespace Engram.Tests.Observer;

/// <summary>Hooks in, observations out, through the real worker with a fake summarizer.</summary>
public sealed class ObserverEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Common(string prompt = "p1") =>
        $"\"session_id\":\"s1\",\"prompt_id\":\"{prompt}\",\"transcript_path\":\"t.jsonl\",\"cwd\":\"C:/work/app\"";

    private static async Task PostAsync(HttpClient client, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/hooks/event", UriKind.Relative), content, Ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }

        return condition();
    }

    [Fact]
    public async Task A_finished_turn_and_session_produce_observations_and_summaries_visible_through_the_api()
    {
        var llm = new FakeLlmClient()
            .Reply("""{"observations":[{"type":"gotcha","title":"Rebuild node-pty after Electron upgrades","narrative":"The native module breaks after an Electron bump. Rebuilding fixes it.","facts":[],"files":["package.json"]}]}""")
            .Reply("""{"request":"upgrade electron","investigated":"node-pty","learned":"needs rebuild","completed":"rebuilt","next_steps":""}""");
        using var factory = new WorkerFactory("""{ "observer": { "provider": "claude-cli" } }""", llm);
        using var client = factory.CreateHookClient();

        await PostAsync(client, $$$"""{"hook_event_name":"SessionStart",{{{Common()}}},"source":"startup"}""");
        await PostAsync(client, $$$"""{"hook_event_name":"UserPromptSubmit",{{{Common()}}},"prompt":"upgrade electron"}""");
        await PostAsync(client, $$$"""{"hook_event_name":"PostToolUse",{{{Common()}}},"tool_name":"Bash","tool_use_id":"t1","tool_input":{"command":"npm rebuild node-pty"},"tool_response":{"stdout":"rebuilt"}}""");
        await PostAsync(client, $$$"""{"hook_event_name":"Stop",{{{Common()}}},"stop_hook_active":false,"last_assistant_message":"Rebuilt node-pty."}""");
        await PostAsync(client, $$$"""{"hook_event_name":"SessionEnd",{{{Common()}}},"reason":"prompt_input_exit"}""");

        (await WaitForAsync(() => factory.Count("SELECT count(*) FROM jobs WHERE status = 'done'") == 2)).ShouldBeTrue("both jobs should finish");

        factory.Count("SELECT count(*) FROM observations").ShouldBe(1);
        factory.Count("SELECT count(*) FROM summaries WHERE kind = 'final'").ShouldBe(1);
        llm.Requests.Count.ShouldBe(2);
        llm.Requests[0].User.ShouldContain("npm rebuild node-pty");

        var recent = await client.GetFromJsonAsync<JsonElement>(new Uri("/recent?limit=5", UriKind.Relative), Ct);
        recent.GetProperty("observations")[0].GetProperty("title").GetString().ShouldBe("Rebuild node-pty after Electron upgrades");
        recent.GetProperty("observations")[0].GetProperty("project").GetString().ShouldBe("app");
        recent.GetProperty("summaries")[0].GetProperty("completed").GetString().ShouldBe("rebuilt");

        var stats = await client.GetFromJsonAsync<JsonElement>(new Uri("/stats", UriKind.Relative), Ct);
        stats.GetProperty("counts").GetProperty("observations").GetInt64().ShouldBe(1);
        stats.GetProperty("jobs").GetProperty("done").GetInt64().ShouldBe(2);
        stats.GetProperty("jobs").GetProperty("pending").GetInt64().ShouldBe(0);
        stats.GetProperty("observer").GetProperty("calls_24h").GetInt64().ShouldBe(2);
        stats.GetProperty("observer").GetProperty("input_tokens_24h").GetInt64().ShouldBe(1400);
    }

    [Fact]
    public async Task With_the_provider_set_to_none_jobs_stay_queued_and_nothing_is_called()
    {
        var llm = new FakeLlmClient();
        using var factory = new WorkerFactory(null, llm); // default settings: provider none... but the fake is wired in
        using var client = factory.CreateHookClient();

        await PostAsync(client, $$$"""{"hook_event_name":"UserPromptSubmit",{{{Common()}}},"prompt":"hi"}""");
        await PostAsync(client, $$$"""{"hook_event_name":"Stop",{{{Common()}}},"stop_hook_active":false,"last_assistant_message":"hello"}""");
        await factory.DrainAsync();

        // The fake runtime is injected regardless of settings, so use a factory without it for the real "none" path.
        using var none = new WorkerFactory();
        using var noneClient = none.CreateHookClient();
        await PostAsync(noneClient, $$$"""{"hook_event_name":"UserPromptSubmit",{{{Common()}}},"prompt":"hi"}""");
        await PostAsync(noneClient, $$$"""{"hook_event_name":"Stop",{{{Common()}}},"stop_hook_active":false,"last_assistant_message":"hello"}""");
        await none.DrainAsync();
        await Task.Delay(300, Ct);

        none.Count("SELECT count(*) FROM jobs WHERE status = 'pending'").ShouldBe(1);
        none.Count("SELECT count(*) FROM llm_calls").ShouldBe(0);
    }
}
