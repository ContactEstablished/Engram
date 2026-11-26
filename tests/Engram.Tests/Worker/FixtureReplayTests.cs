using System.Net;
using System.Net.Http.Headers;

namespace Engram.Tests.Worker;

/// <summary>
/// Contract test: every real Phase 0 payload, replayed against the worker, is accepted with an empty 200
/// and lands where the capture rules say it should.
/// </summary>
public sealed class FixtureReplayTests
{
    private static readonly string FixtureDir = Path.Combine(RepoPaths.Root, "tests", "fixtures", "hooks");

    // Roughly the order Claude Code fires them.
    private static readonly string[] Order =
        ["SessionStart.startup", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PostToolBatch",
         "SubagentStop", "Stop", "PreCompact", "PostCompact", "SessionStart.compact", "SessionStart.clear", "SessionStart.resume", "SessionEnd"];

    [Fact]
    public async Task Real_fixtures_replay_cleanly()
    {
        var files = Directory.GetFiles(FixtureDir, "*.json")
            .OrderBy(f => Array.FindIndex(Order, o => Path.GetFileNameWithoutExtension(f).StartsWith(o, StringComparison.Ordinal)))
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToArray();
        files.Length.ShouldBeGreaterThan(20, "real fixtures from Phase 0");

        using var factory = new WorkerFactory();
        using var client = factory.CreateHookClient();
        foreach (var file in files)
        {
            using var content = new ByteArrayContent(File.ReadAllBytes(file));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync(new Uri("/hooks/event", UriKind.Relative), content, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, Path.GetFileName(file));
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        }

        await factory.DrainAsync();

        var tools = factory.Query<string>("SELECT DISTINCT tool_name FROM events WHERE tool_name IS NOT NULL ORDER BY 1");
        tools.ShouldBe(["Agent", "Bash", "Edit", "WebFetch", "Write"], ignoreOrder: true);
        factory.Count("SELECT count(*) FROM events WHERE hook_event = 'PostToolUseFailure' AND is_error = 1").ShouldBe(1);
        factory.Count("SELECT count(*) FROM events WHERE hook_event = 'Stop'").ShouldBe(1);
        factory.Count("SELECT count(*) FROM prompts").ShouldBe(1);
        factory.Count("SELECT count(*) FROM jobs WHERE kind = 'observe-turn'").ShouldBe(1);
        factory.Count("SELECT count(*) FROM jobs WHERE kind = 'summarize-checkpoint'").ShouldBe(1);
        factory.Count("SELECT count(*) FROM jobs WHERE kind = 'summarize-session'").ShouldBe(1);
        factory.Count("SELECT count(*) FROM sessions WHERE source IS NOT NULL").ShouldBeGreaterThan(0);
        factory.Count("SELECT count(*) FROM events WHERE tool_response LIKE '%originalFile\":\"<omitted>%'").ShouldBe(1, "Edit's whole-file copy is dropped");
    }
}
