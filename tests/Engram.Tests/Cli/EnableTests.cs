using System.Diagnostics;
using System.Text.Json.Nodes;
using Engram.Cli.Commands;
using Engram.Cli.Projects;

namespace Engram.Tests.Cli;

/// <summary>Real git, fake Claude: records the claude calls instead of touching ~/.claude.json.</summary>
internal sealed class TestTools : IProjectTools
{
    private readonly SystemProjectTools _real = new();

    public List<string> ClaudeCalls { get; } = [];

    public (int ExitCode, string Output) ClaudeResult { get; set; } = (0, "Added stdio MCP server engram");

    public string? Git(string dir, params string[] args) => _real.Git(dir, args);

    public (int ExitCode, string Output) Claude(string dir, params string[] args)
    {
        ClaudeCalls.Add(string.Join(' ', args));
        return ClaudeResult;
    }
}

public sealed class EnableTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly TestTools _tools = new();
    private readonly ProjectEnabler _enabler;

    public EnableTests() => _enabler = new ProjectEnabler(_tools, _temp.Combine("home"), 47613, "Edit|Bash", TimeProvider.System);

    public void Dispose() => _temp.Dispose();

    private string Repo(string name = "app", bool commit = true)
    {
        var dir = _temp.Combine(name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        // A repo-level empty excludes file overrides any global gitignore on the machine running the tests.
        var noGlobal = _temp.Combine("no-global-ignore");
        File.WriteAllText(noGlobal, string.Empty);
        Git(dir, "config", "core.excludesFile", noGlobal);
        if (commit)
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
            Git(dir, "add", "a.txt");
            Git(dir, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init");
        }

        return dir;
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(cwd);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        p.ExitCode.ShouldBe(0, p.StandardError.ReadToEnd());
    }

    private static JsonObject Read(string repo, string relative = ".claude/settings.local.json") =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(repo, relative)))!;

    private static string GitStatus(string repo)
    {
        var psi = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--porcelain");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    [Fact]
    public void A_fresh_project_gets_the_hooks_registered_the_mcp_server_and_stays_clean_in_git()
    {
        var repo = Repo();

        var change = _enabler.Enable(repo, McpMode.Local, dryRun: false);

        var hooks = Read(repo)["hooks"]!.AsObject();
        hooks.Select(h => h.Key).ShouldBe(HookSettings.Events, ignoreOrder: true);
        hooks["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>().ShouldBe("engram");
        hooks["PostToolUse"]![0]!["matcher"]!.GetValue<string>().ShouldBe("Edit|Bash");
        hooks["Stop"]![0]!["hooks"]![0]!["url"]!.GetValue<string>().ShouldBe("http://127.0.0.1:47613/hooks/stop");
        hooks["Stop"]![0]!["hooks"]![0]!["headers"]!["Authorization"]!.GetValue<string>().ShouldBe("Bearer $ENGRAM_TOKEN");
        _tools.ClaudeCalls.ShouldBe(["mcp add --scope local engram -- engram mcp"]);
        GitStatus(repo).ShouldBeEmpty("the settings file is excluded locally, so the repo stays clean");
        File.ReadAllText(Path.Combine(repo, ".git", "info", "exclude")).ShouldContain(".claude/settings.local.json");
        change.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Existing_settings_and_other_hooks_survive_and_the_original_is_backed_up()
    {
        var repo = Repo();
        Directory.CreateDirectory(Path.Combine(repo, ".claude"));
        File.WriteAllText(Path.Combine(repo, ".claude", "settings.local.json"), """
            {
              "permissions": { "allow": ["Bash(git status)"] },
              "env": { "FOO": "bar" },
              "hooks": {
                "PostToolUse": [ { "matcher": "Write", "hooks": [ { "type": "command", "command": "my-linter" } ] } ],
                "Notification": [ { "hooks": [ { "type": "command", "command": "notify-me" } ] } ]
              }
            }
            """);

        _enabler.Enable(repo, McpMode.None, dryRun: false);
        var root = Read(repo);

        root["permissions"]!["allow"]![0]!.GetValue<string>().ShouldBe("Bash(git status)");
        root["env"]!["FOO"]!.GetValue<string>().ShouldBe("bar");
        root["hooks"]!["Notification"]![0]!["hooks"]![0]!["command"]!.GetValue<string>().ShouldBe("notify-me");
        var post = root["hooks"]!["PostToolUse"]!.AsArray();
        post.Count.ShouldBe(2, "the user's linter hook and Engram's");
        post[0]!["hooks"]![0]!["command"]!.GetValue<string>().ShouldBe("my-linter");
        Directory.GetFiles(Path.Combine(_temp.Path, "home", "backups", "enable"), "*.bak").ShouldHaveSingleItem();
        File.ReadAllText(Directory.GetFiles(Path.Combine(_temp.Path, "home", "backups", "enable"))[0]).ShouldContain("my-linter");
        _tools.ClaudeCalls.ShouldBeEmpty();
    }

    [Fact]
    public void Enabling_twice_changes_nothing_and_a_new_port_replaces_the_old_one()
    {
        var repo = Repo();
        _enabler.Enable(repo, McpMode.None, dryRun: false);
        var first = File.ReadAllText(Path.Combine(repo, ".claude", "settings.local.json"));

        var again = _enabler.Enable(repo, McpMode.None, dryRun: false);
        again.Actions.ShouldContain(a => a.Contains("already registered", StringComparison.Ordinal));
        File.ReadAllText(Path.Combine(repo, ".claude", "settings.local.json")).ShouldBe(first);

        var wsl = new ProjectEnabler(_tools, _temp.Combine("home"), 47614, "Edit|Bash", TimeProvider.System);
        var moved = wsl.Enable(repo, McpMode.None, dryRun: false);

        moved.Actions.ShouldContain("hook port changed 47613 -> 47614");
        Read(repo)["hooks"]!["Stop"]![0]!["hooks"]![0]!["url"]!.GetValue<string>().ShouldBe("http://127.0.0.1:47614/hooks/stop");
        Read(repo)["hooks"]!["Stop"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void Disable_removes_only_engrams_entries_and_deletes_a_file_that_held_nothing_else()
    {
        var repo = Repo();
        _enabler.Enable(repo, McpMode.None, dryRun: false);
        _enabler.Disable(repo, dryRun: false);
        File.Exists(Path.Combine(repo, ".claude", "settings.local.json")).ShouldBeFalse();

        File.WriteAllText(Path.Combine(repo, ".claude", "settings.local.json"), """{ "env": { "A": "1" }, "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "mine" } ] } ] } }""");
        _enabler.Enable(repo, McpMode.None, dryRun: false);
        var change = _enabler.Disable(repo, dryRun: false);

        change.Actions.ShouldContain(a => a.Contains("other settings kept", StringComparison.Ordinal));
        var root = Read(repo);
        root["env"]!["A"]!.GetValue<string>().ShouldBe("1");
        root["hooks"]!.AsObject().Select(h => h.Key).ShouldBe(["Stop"]);
        root["hooks"]!["Stop"]!.AsArray().Count.ShouldBe(1);
        root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>().ShouldBe("mine");
        _tools.ClaudeCalls.ShouldContain("mcp remove engram --scope local");
    }

    [Fact]
    public void A_dry_run_writes_nothing_and_runs_nothing()
    {
        var repo = Repo();

        var change = _enabler.Enable(repo, McpMode.Local, dryRun: true);

        change.Actions.Count.ShouldBeGreaterThan(1);
        Directory.Exists(Path.Combine(repo, ".claude")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_temp.Path, "home")).ShouldBeFalse();
        _tools.ClaudeCalls.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(repo, ".git", "info", "exclude")).ShouldNotContain("settings.local");
    }

    [Fact]
    public void The_file_mcp_mode_writes_mcp_json_merges_and_excludes_it()
    {
        var repo = Repo();
        File.WriteAllText(Path.Combine(repo, ".mcp.json"), """{ "mcpServers": { "other": { "command": "x" } } }""");

        _enabler.Enable(repo, McpMode.File, dryRun: false);
        var servers = Read(repo, ".mcp.json")["mcpServers"]!.AsObject();

        servers.Select(s => s.Key).ShouldBe(["other", "engram"]);
        servers["engram"]!["command"]!.GetValue<string>().ShouldBe("engram");
        File.ReadAllText(Path.Combine(repo, ".git", "info", "exclude")).ShouldContain(".mcp.json");

        _enabler.Disable(repo, dryRun: false);
        Read(repo, ".mcp.json")["mcpServers"]!.AsObject().Select(s => s.Key).ShouldBe(["other"]);
    }

    [Fact]
    public void Worktrees_are_found_and_each_gets_its_own_settings()
    {
        var repo = Repo("taxapp");
        var feature = _temp.Combine("taxapp-feature");
        Git(repo, "worktree", "add", "-q", "-b", "feature", feature);

        var trees = _enabler.ExpandWorktrees(repo);

        trees.Count.ShouldBe(2);
        foreach (var tree in trees)
        {
            _enabler.Enable(tree, McpMode.None, dryRun: false);
            File.Exists(Path.Combine(tree, ".claude", "settings.local.json")).ShouldBeTrue(tree);
            GitStatus(tree).ShouldBeEmpty(tree);
        }
    }

    [Theory]
    [InlineData("{ // comment\n \"env\": {} }", "comments")]
    [InlineData("{ not json", "not valid JSON")]
    [InlineData("[1,2]", "not a JSON object")]
    public void Files_that_cant_be_rewritten_safely_are_refused_and_left_alone(string content, string reason)
    {
        var repo = Repo();
        Directory.CreateDirectory(Path.Combine(repo, ".claude"));
        var path = Path.Combine(repo, ".claude", "settings.local.json");
        File.WriteAllText(path, content);

        Should.Throw<ArgumentException>(() => _enabler.Enable(repo, McpMode.None, dryRun: false)).Message.ShouldContain(reason);

        File.ReadAllText(path).ShouldBe(content);
    }

    [Fact]
    public void A_folder_inside_a_repo_and_a_tracked_settings_file_produce_warnings()
    {
        var repo = Repo();
        var sub = Path.Combine(repo, "packages", "web");
        Directory.CreateDirectory(Path.Combine(sub, ".claude"));
        File.WriteAllText(Path.Combine(repo, "a.txt"), "x");
        File.WriteAllText(Path.Combine(sub, ".claude", "settings.local.json"), "{}");
        Git(repo, "add", "-f", "packages/web/.claude/settings.local.json");

        var change = _enabler.Enable(sub, McpMode.None, dryRun: true);

        change.Warnings.ShouldContain(w => w.Contains("is inside the repo", StringComparison.Ordinal));
        change.Warnings.ShouldContain(w => w.Contains("tracked by git", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_mcp_registration_is_a_warning_not_a_failure()
    {
        var repo = Repo();
        _tools.ClaudeResult = (-1, "not found");

        var change = _enabler.Enable(repo, McpMode.Local, dryRun: false);

        change.Warnings.ShouldContain(w => w.Contains("--mcp file", StringComparison.Ordinal));
        File.Exists(Path.Combine(repo, ".claude", "settings.local.json")).ShouldBeTrue("the hooks are still registered");

        _tools.ClaudeResult = (1, "MCP server engram already exists in local config");
        _enabler.Enable(repo, McpMode.Local, dryRun: false).Actions.ShouldContain(a => a.Contains("already registered", StringComparison.Ordinal));
    }

    [Fact]
    public void WSL_paths_and_missing_folders_are_rejected()
    {
        Should.Throw<ArgumentException>(() => _enabler.Enable(@"\\wsl.localhost\Ubuntu\home\x\app", McpMode.None, dryRun: true)).Message.ShouldContain("inside WSL");
        Should.Throw<ArgumentException>(() => _enabler.Enable(_temp.Combine("nope"), McpMode.None, dryRun: true)).Message.ShouldContain("not a folder");
    }

    [Fact]
    public void The_command_plans_asks_then_applies_and_reports_per_folder()
    {
        var a = Repo("a");
        var b = Repo("b");
        var output = new StringWriter();
        Func<string, string?> env = name => name == "ENGRAM_HOME" ? _temp.Combine("home") : null;
        Engram.Core.Security.WorkerToken.ReadOrCreate(_temp.Combine("home"));

        EnableCommand.Run(true, EnableCommand.Parse([a, b, _temp.Combine("missing"), "--mcp", "none"]), env, output, new StringReader("n\n"), _tools, TimeProvider.System).ShouldBe(0);
        File.Exists(Path.Combine(a, ".claude", "settings.local.json")).ShouldBeFalse("answered no");
        output.ToString().ShouldContain("SKIP:");
        output.ToString().ShouldContain("Nothing changed.");

        var yes = new StringWriter();
        EnableCommand.Run(true, EnableCommand.Parse([a, b, "--mcp", "none", "--yes"]), env, yes, TextReader.Null, _tools, TimeProvider.System).ShouldBe(0);
        File.Exists(Path.Combine(a, ".claude", "settings.local.json")).ShouldBeTrue();
        File.Exists(Path.Combine(b, ".claude", "settings.local.json")).ShouldBeTrue();
        yes.ToString().ShouldContain("restart the app that launches Claude Code");

        var off = new StringWriter();
        EnableCommand.Run(false, EnableCommand.Parse([a, b, "--yes"]), env, off, TextReader.Null, _tools, TimeProvider.System).ShouldBe(0);
        File.Exists(Path.Combine(a, ".claude", "settings.local.json")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("--mcp", "bogus")]
    [InlineData("--nope", "")]
    public void Bad_options_are_rejected(string first, string second) =>
        Should.Throw<ArgumentException>(() => EnableCommand.Parse(second.Length == 0 ? [first] : [first, second]));

    [Fact]
    public void No_folder_means_the_current_one() => EnableCommand.Parse([]).Folders.ShouldBe(["."]);
}
