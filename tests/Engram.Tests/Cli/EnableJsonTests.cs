using System.Diagnostics;
using System.Text.Json;
using Engram.Cli.Commands;
using Engram.Cli.Projects;
using Engram.Core.Security;

namespace Engram.Tests.Cli;

/// <summary>The machine-readable contract Chorus parses: `--json` lines, `engram state`, `engram status --json`.</summary>
public sealed class EnableJsonTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly TestTools _tools = new();
    private readonly string _home;
    private readonly Func<string, string?> _env;

    public EnableJsonTests()
    {
        _home = _temp.Combine("home");
        WorkerToken.ReadOrCreate(_home);
        _env = name => name switch
        {
            "ENGRAM_HOME" => _home,
            "ENGRAM_PORT" => "47991", // nothing listens here: status sees a stopped worker
            _ => null,
        };
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A repo in a folder whose path has spaces, like "AutoVitals Projects".</summary>
    private string Repo(string name)
    {
        var dir = _temp.Combine("My Projects", name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        var noGlobal = _temp.Combine("no-global-ignore");
        File.WriteAllText(noGlobal, string.Empty);
        Git(dir, "config", "core.excludesFile", noGlobal);
        Git(dir, "remote", "add", "origin", "git@github.com:Example/Sample-App.git");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        Git(dir, "add", "a.txt");
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init");
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

    private (int Exit, List<JsonElement> Lines, string Stderr) Run(string verb, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = EnableCommand.Run(verb, EnableCommand.Parse(args), _env, stdout, stderr, TextReader.Null, noTerminal: true, _tools, TimeProvider.System);
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();
        return (exit, lines, stderr.ToString());
    }

    private static string[] Strings(JsonElement e, string name) => [.. e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!)];

    [Fact]
    public void A_dry_run_with_worktrees_prints_one_planned_line_per_folder_and_changes_nothing()
    {
        var repo = Repo("Sample App");
        var feature = _temp.Combine("My Projects", "Sample App feature");
        Git(repo, "worktree", "add", "-q", "-b", "feature", feature);

        var (exit, lines, stderr) = Run("enable", repo, "--worktrees", "--dry-run", "--json");

        exit.ShouldBe(0);
        lines.Count.ShouldBe(2);
        lines[0].GetProperty("folder").GetString().ShouldBe(repo);
        lines[0].GetProperty("result").GetString().ShouldBe("planned");
        lines[0].GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null);
        lines[0].GetProperty("worktree_of").ValueKind.ShouldBe(JsonValueKind.Null);
        Strings(lines[0], "files").ShouldContain(Path.Combine(repo, ".claude", "settings.local.json"));
        Strings(lines[0], "actions").ShouldContain(a => a.Contains("claude mcp add", StringComparison.Ordinal));
        lines[1].GetProperty("folder").GetString().ShouldBe(feature);
        lines[1].GetProperty("worktree_of").GetString().ShouldBe(repo);
        stderr.ShouldContain("Dry run", Case.Sensitive, "the human-readable plan goes to stderr");
        Directory.Exists(Path.Combine(repo, ".claude")).ShouldBeFalse();
        Directory.Exists(Path.Combine(feature, ".claude")).ShouldBeFalse();
        _tools.ClaudeCalls.ShouldBeEmpty();
    }

    [Fact]
    public void Without_yes_and_without_a_terminal_it_refuses_instead_of_prompting()
    {
        var repo = Repo("app");

        var (exit, lines, stderr) = Run("enable", repo, "--json");

        exit.ShouldBe(1);
        var line = lines.ShouldHaveSingleItem();
        line.GetProperty("result").GetString().ShouldBe("skipped");
        line.GetProperty("reason").GetString().ShouldNotBeNull().ShouldContain("--yes");
        stderr.ShouldContain("pass --yes");
        Directory.Exists(Path.Combine(repo, ".claude")).ShouldBeFalse();
    }

    [Fact]
    public void Enable_reports_done_with_files_then_state_reports_enabled_and_disable_turns_it_off()
    {
        var repo = Repo("app");

        var (exit, lines, _) = Run("enable", repo, "--yes", "--json");

        exit.ShouldBe(0);
        var done = lines.ShouldHaveSingleItem();
        done.GetProperty("result").GetString().ShouldBe("done");
        Strings(done, "files").ShouldContain(Path.Combine(repo, ".claude", "settings.local.json"));
        Strings(done, "files").ShouldContain(f => f.EndsWith("exclude", StringComparison.Ordinal));
        _tools.ClaudeCalls.ShouldContain("mcp add --scope local engram -- engram mcp");

        var state = Run("state", repo, "--json").Lines.ShouldHaveSingleItem();
        state.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        state.GetProperty("hooks_ok").GetBoolean().ShouldBeTrue();
        state.GetProperty("mcp").GetString().ShouldBe("local");
        state.GetProperty("problems").GetArrayLength().ShouldBe(0);
        state.GetProperty("project").GetString().ShouldBe("github.com/example/sample-app");
        state.GetProperty("worktree_of").ValueKind.ShouldBe(JsonValueKind.Null);

        var planned = Run("disable", repo, "--dry-run", "--json").Lines.ShouldHaveSingleItem();
        planned.GetProperty("result").GetString().ShouldBe("planned");
        Strings(planned, "files").ShouldContain(Path.Combine(repo, ".claude", "settings.local.json"));
        File.Exists(Path.Combine(repo, ".claude", "settings.local.json")).ShouldBeTrue("dry run");

        Run("disable", repo, "--yes", "--json").Lines.ShouldHaveSingleItem().GetProperty("result").GetString().ShouldBe("done");
        var off = Run("state", repo, "--json").Lines.ShouldHaveSingleItem();
        off.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        off.GetProperty("mcp").GetString().ShouldBe("none");
    }

    [Fact]
    public void State_flags_hooks_that_need_repair()
    {
        var repo = Repo("app");
        Run("enable", repo, "--yes", "--json", "--mcp", "none");
        var path = Path.Combine(repo, ".claude", "settings.local.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("47991", "47000", StringComparison.Ordinal));
        var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        settings["hooks"]!.AsObject().Remove("Stop");
        File.WriteAllText(path, settings.ToJsonString());

        var state = Run("state", repo, "--json").Lines.ShouldHaveSingleItem();

        state.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        state.GetProperty("hooks_ok").GetBoolean().ShouldBeFalse();
        var problems = Strings(state, "problems");
        problems.ShouldContain("missing the Stop hook");
        problems.ShouldContain(p => p.Contains("old port 47000 vs 47991", StringComparison.Ordinal), string.Join(" | ", problems));

        Run("enable", repo, "--yes", "--json", "--mcp", "none");
        Run("state", repo, "--json").Lines.ShouldHaveSingleItem().GetProperty("hooks_ok").GetBoolean().ShouldBeTrue("enable repairs");
    }

    [Fact]
    public void State_of_a_worktree_names_its_main_folder_and_shares_the_project()
    {
        var repo = Repo("app");
        var feature = _temp.Combine("My Projects", "app feature");
        Git(repo, "worktree", "add", "-q", "-b", "feature", feature);

        var lines = Run("state", repo, "--worktrees", "--json").Lines;

        lines.Count.ShouldBe(2);
        lines[1].GetProperty("worktree_of").GetString().ShouldBe(repo);
        lines.Select(l => l.GetProperty("project").GetString()).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public void Missing_folders_are_skipped_per_folder_without_hiding_the_others()
    {
        var repo = Repo("app");

        var (exit, lines, _) = Run("enable", repo, _temp.Combine("nope"), "--yes", "--json", "--mcp", "none");

        exit.ShouldBe(1);
        lines.Count.ShouldBe(2);
        lines[0].GetProperty("result").GetString().ShouldBe("done");
        lines[1].GetProperty("result").GetString().ShouldBe("skipped");
        lines[1].GetProperty("reason").GetString().ShouldNotBeNull().ShouldContain("not a folder");
        Run("state", _temp.Combine("nope"), "--json").Lines.ShouldHaveSingleItem().GetProperty("error").GetString().ShouldNotBeNull();
    }

    [Theory]
    [InlineData("enable", "--worktrees")]
    [InlineData("disable", "--dry-run")]
    [InlineData("state", "hooks_ok")]
    public void Each_command_has_help(string verb, string mentions)
    {
        var output = new StringWriter();

        EnableCommand.Run(verb, EnableCommand.Parse(["--help"]), _env, output, output, TextReader.Null, true, _tools, TimeProvider.System).ShouldBe(0);

        output.ToString().ShouldContain(mentions);
    }

    [Fact]
    public void Status_json_is_valid_exits_zero_with_the_worker_down_and_never_contains_the_token()
    {
        var output = new StringWriter();

        StatusCommand.RunJson(_env, output).ShouldBe(0);

        var text = output.ToString();
        var root = JsonDocument.Parse(text).RootElement;
        root.GetProperty("version").GetString().ShouldBe("0.5.0");
        root.GetProperty("home").GetString().ShouldBe(_home);
        root.GetProperty("worker").GetProperty("running").GetBoolean().ShouldBeFalse();
        root.GetProperty("worker").GetProperty("port").GetInt32().ShouldBe(47991);
        root.GetProperty("token_present").GetBoolean().ShouldBeTrue();
        root.GetProperty("token_in_environment").GetBoolean().ShouldBeFalse();
        root.GetProperty("observations").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("spooled").GetInt32().ShouldBe(0);
        text.ShouldNotContain(WorkerToken.Read(_home)!);
    }

    [Fact]
    public void The_real_binary_speaks_json_for_a_folder_with_spaces()
    {
        var repo = Repo("Spaced Repo");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "engram.dll"));
        foreach (var a in new[] { "enable", repo, "--dry-run", "--json", "--mcp", "none" })
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["ENGRAM_HOME"] = _home;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000).ShouldBeTrue();

        p.ExitCode.ShouldBe(0, p.StandardError.ReadToEnd());
        var line = JsonDocument.Parse(stdout.Trim()).RootElement;
        line.GetProperty("folder").GetString().ShouldBe(repo);
        line.GetProperty("result").GetString().ShouldBe("planned");
    }

    [Fact]
    public void Claude_is_found_outside_PATH_in_the_native_install_location()
    {
        // Under `wsl.exe -e` there is no login shell, so ~/.local/bin isn't on PATH; the locator must still find it.
        var found = SystemProjectTools.LocateClaude();

        if (found is not null)
        {
            File.Exists(found).ShouldBeTrue();
        }
    }
}
