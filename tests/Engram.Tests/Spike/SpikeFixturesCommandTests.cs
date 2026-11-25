using System.Text.Json;
using Engram.Cli.Spike;
using Engram.Core.Hooks;

namespace Engram.Tests.Spike;

public sealed class SpikeFixturesCommandTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Line(string payload, double processMs = 40, bool wsl = false) =>
        $$"""{"received_at":"2026-11-25T10:00:00-05:00","handler_ms":1.5,"process_ms":{{processMs}},"os":"{{(wsl ? "linux" : "windows")}}","is_wsl":{{(wsl ? "true" : "false")}},"payload":{{payload}}}""";

    private const string Common = """
        "session_id":"s","transcript_path":"C:\\Users\\jdoe\\.claude\\t.jsonl","cwd":"C:\\Users\\jdoe\\sandbox"
        """;

    private void WriteCapture(string dir, string file, params string[] lines)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, file), lines);
    }

    [Fact]
    public void Writes_one_sanitized_fixture_per_event_tool_and_source()
    {
        var win = _temp.Combine("win");
        var wsl = _temp.Combine("wsl");
        var outDir = _temp.Combine("out");
        WriteCapture(win, "PostToolUse.jsonl",
            Line($$"""{"hook_event_name":"PostToolUse",{{Common}},"tool_name":"Bash","tool_input":{"command":"ls"},"tool_response":{"stdout":"a"},"tool_use_id":"t1"}"""),
            Line($$"""{"hook_event_name":"PostToolUse",{{Common}},"tool_name":"Bash","tool_input":{"command":"whoami"},"tool_response":{"stdout":"jdoe","stderr":"","interrupted":false},"tool_use_id":"t2"}"""),
            Line($$"""{"hook_event_name":"PostToolUse",{{Common}},"tool_name":"Edit","tool_input":{"file_path":"x"},"tool_response":{"ok":true},"tool_use_id":"t3"}"""));
        WriteCapture(win, "SessionStart.jsonl",
            Line($$"""{"hook_event_name":"SessionStart",{{Common}},"source":"startup"}"""),
            Line($$"""{"hook_event_name":"SessionStart",{{Common}},"source":"compact"}"""));
        WriteCapture(wsl, "SessionStart.jsonl",
            Line("""{"hook_event_name":"SessionStart","session_id":"s","transcript_path":"/home/wsluser/.claude/t","cwd":"/home/wsluser/sandbox","source":"startup"}""", wsl: true));
        WriteCapture(win, "http.PostToolUse.jsonl", """{"headers":{"Authorization":"Bearer abcd…"},"authorization":{"matches_env":true}}""");
        var report = new StringWriter();

        var exit = SpikeFixturesCommand.Run(new SpikeFixturesCommand.Options([win, wsl], outDir, [], 2000), [("jdoe", "<USER>")], report);

        exit.ShouldBe(0);
        Directory.GetFiles(outDir).Select(Path.GetFileName).ShouldBe(
            ["PostToolUse.Bash.json", "PostToolUse.Edit.json", "SessionStart.compact.json", "SessionStart.startup.json"], ignoreOrder: true);

        // The richer Bash record wins, and personal data is gone.
        var bash = File.ReadAllText(Path.Combine(outDir, "PostToolUse.Bash.json"));
        bash.ShouldContain("whoami");
        bash.ShouldNotContain("jdoe", Case.Insensitive);
        bash.ShouldContain("<HOME>");
        FixtureSanitizer.FindLeaks(bash).ShouldBeEmpty();
        HookPayloadReader.Read(File.ReadAllBytes(Path.Combine(outDir, "PostToolUse.Bash.json"))).ShouldBeOfType<PostToolUsePayload>();

        // Windows wins ties; the WSL user learned from /home/<name> is redacted in the report's source too.
        var startup = File.ReadAllText(Path.Combine(outDir, "SessionStart.startup.json"));
        startup.ShouldNotContain("wsluser");

        var text = report.ToString();
        text.ShouldContain("## PostToolUse");
        text.ShouldContain("tool Bash (2)");
        text.ShouldContain("sources: startup ×2; compact ×1");
        text.ShouldContain("(WSL)");
        text.ShouldContain("authorization.matches_env: True ×1");
    }

    [Fact]
    public void Returns_1_when_nothing_was_captured()
    {
        var report = new StringWriter();

        SpikeFixturesCommand.Run(new SpikeFixturesCommand.Options([_temp.Combine("missing")], _temp.Combine("out"), [], 2000), [], report).ShouldBe(1);

        report.ToString().ShouldContain("No captures found");
    }

    [Theory]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_name":"mcp__engram__search"}""", "PreToolUse.mcp__engram__search")]
    [InlineData("""{"hook_event_name":"SessionStart","source":"fork"}""", "SessionStart.fork")]
    [InlineData("""{"hook_event_name":"PostToolBatch","tool_calls":[]}""", "PostToolBatch")]
    [InlineData("""{"hook_event_name":"Stop"}""", "Stop")]
    public void Key_includes_tool_name_or_source(string payload, string expected)
    {
        using var doc = JsonDocument.Parse(payload);

        SpikeFixturesCommand.KeyFor(doc.RootElement.GetProperty("hook_event_name").GetString()!, doc.RootElement).ShouldBe(expected);
    }
}
