using System.Text;
using System.Text.Json;
using Engram.Cli.Projects;
using Engram.Core;
using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Cli.Commands;

/// <summary>
/// <c>engram enable|disable|state [FOLDER...]</c>: switch Engram on or off for project folders, or report whether it
/// is on. Made to be called by people (plan, then a y/N question) and by programs (<c>--json</c>: one JSON object
/// per folder per line on stdout, human text on stderr, never a prompt).
/// </summary>
internal static class EnableCommand
{
    public const string EnableHelp = """
        engram enable [FOLDER...] [--worktrees] [--mcp local|file|none] [--dry-run] [--yes] [--json]

        Turns Engram on for project folders (default: the current folder): merges Engram's hooks into
        FOLDER/.claude/settings.local.json (other settings kept, original backed up to ~/.engram/backups/enable;
        files with comments or invalid JSON are refused), registers the MCP server, and keeps git status clean
        through .git/info/exclude.

          --worktrees      also every other git worktree of each folder's repository
          --mcp MODE       local (default): `claude mcp add --scope local`, nothing added to the repo
                           file: a .mcp.json in the folder;  none: hooks only
          --dry-run        show what would change; change nothing
          --yes            don't ask (required when there is no terminal, e.g. called by another program)
          --json           one JSON object per folder per line on stdout:
                           {"folder","result":"done|failed|skipped|planned","reason","actions","warnings","files","worktree_of"}
        Exit code: 0 when every folder is done (or planned), 1 otherwise.
        """;

    public const string DisableHelp = """
        engram disable [FOLDER...] [--worktrees] [--dry-run] [--yes] [--json]

        Removes Engram's hooks and MCP registration from project folders, leaving everything else. Deletes
        .claude/settings.local.json if it held only Engram's hooks. Same options and JSON output as `engram enable`.
        """;

    public const string StateHelp = """
        engram state [FOLDER...] [--worktrees] [--json]

        Read-only: is Engram on for each folder, and are its hooks complete and current? Never starts the worker.
          --json   one JSON object per folder per line:
                   {"folder","enabled","mcp":"local|file|none","hooks_ok","problems","worktree_of","project"}
                   or {"folder","error"} for a folder that can't be inspected.
        Exit code: 0 (the answer is in the output), 1 only if a folder doesn't exist.
        """;

    internal sealed record Options(IReadOnlyList<string> Folders, bool Worktrees, McpMode Mcp, bool Yes, bool DryRun, bool Json, bool Help);

    public static int Run(string verb, string[] args, Func<string, string?> getEnv, TextWriter output, TextWriter error, TextReader input) =>
        Run(verb, Parse(args), getEnv, output, error, input, Console.IsInputRedirected, new SystemProjectTools(), TimeProvider.System);

    internal static Options Parse(string[] args)
    {
        var folders = new List<string>();
        bool worktrees = false, yes = false, dry = false, json = false, help = false;
        var mcp = McpMode.Local;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--worktrees" or "-w": worktrees = true; break;
                case "--yes" or "-y": yes = true; break;
                case "--dry-run": dry = true; break;
                case "--json": json = true; break;
                case "--help" or "-h": help = true; break;
                case "--mcp" when i + 1 < args.Length:
                    mcp = args[++i].ToLowerInvariant() switch
                    {
                        "local" => McpMode.Local,
                        "file" => McpMode.File,
                        "none" => McpMode.None,
                        var other => throw new ArgumentException($"--mcp must be local, file or none (not '{other}')"),
                    };
                    break;
                case var a when a.StartsWith('-'):
                    throw new ArgumentException($"unknown option '{a}'");
                default:
                    folders.Add(args[i]);
                    break;
            }
        }

        return new Options(folders.Count == 0 ? ["."] : folders, worktrees, mcp, yes, dry, json, help);
    }

    internal static int Run(string verb, Options options, Func<string, string?> getEnv, TextWriter output, TextWriter error, TextReader input, bool noTerminal, IProjectTools tools, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (options.Help)
        {
            output.WriteLine(verb switch { "enable" => EnableHelp, "disable" => DisableHelp, _ => StateHelp });
            return 0;
        }

        var home = EngramPaths.Home(getEnv);
        var (settings, _) = SettingsLoader.Load(home, getEnv);
        var enabler = new ProjectEnabler(tools, home, settings.Worker.Port, settings.Capture.ToolMatcher, time);

        // Folders, each with the main working tree it was reached through (--worktrees).
        var targets = new List<(string Folder, string? WorktreeOf)>();
        foreach (var folder in options.Folders)
        {
            var full = Path.GetFullPath(folder);
            var expanded = options.Worktrees && Directory.Exists(full) && !IsWslPath(full) ? enabler.ExpandWorktrees(full) : [(full, null)];
            foreach (var target in expanded)
            {
                if (!targets.Any(t => string.Equals(t.Folder, target.Folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                {
                    targets.Add(target);
                }
            }
        }

        return verb == "state"
            ? RunState(targets, enabler, options, output)
            : RunChange(verb == "enable", targets, enabler, options, settings.Worker.Port, home, getEnv, output, error, input, noTerminal);
    }

    private static int RunState(List<(string Folder, string? WorktreeOf)> targets, ProjectEnabler enabler, Options options, TextWriter output)
    {
        var missing = 0;
        foreach (var (folder, _) in targets)
        {
            try
            {
                var s = enabler.State(folder);
                if (options.Json)
                {
                    output.WriteLine(Json(w =>
                    {
                        w.WriteString("folder", s.Folder);
                        w.WriteBoolean("enabled", s.Enabled);
                        w.WriteString("mcp", s.Mcp);
                        w.WriteBoolean("hooks_ok", s.HooksOk);
                        Strings(w, "problems", s.Problems);
                        NullableString(w, "worktree_of", s.WorktreeOf);
                        w.WriteString("project", s.Project);
                    }));
                }
                else
                {
                    output.WriteLine($"{s.Folder}: {(s.Enabled ? s.HooksOk ? "on" : "on, needs repair" : "off")} (mcp {s.Mcp}; project {s.Project}{(s.WorktreeOf is null ? string.Empty : "; worktree of " + s.WorktreeOf)})");
                    foreach (var p in s.Problems)
                    {
                        output.WriteLine($"  ! {p}");
                    }
                }
            }
            catch (ArgumentException ex)
            {
                missing++;
                output.WriteLine(options.Json ? Json(w => { w.WriteString("folder", folder); w.WriteString("error", ex.Message); }) : $"{folder}: {ex.Message}");
            }
        }

        return missing == 0 ? 0 : 1;
    }

    private static int RunChange(bool enable, List<(string Folder, string? WorktreeOf)> targets, ProjectEnabler enabler, Options options, int port, string home, Func<string, string?> getEnv, TextWriter output, TextWriter error, TextReader input, bool noTerminal)
    {
        var verb = enable ? "enable" : "disable";

        // Plan first (a dry run of every folder), so nothing changes before it has been seen.
        var plan = new List<(string Folder, string? WorktreeOf, ProjectChange? Change, string? Error)>();
        foreach (var (folder, of) in targets)
        {
            try
            {
                plan.Add((folder, of, enable ? enabler.Enable(folder, options.Mcp, dryRun: true) : enabler.Disable(folder, dryRun: true), null));
            }
            catch (ArgumentException ex)
            {
                plan.Add((folder, of, null, ex.Message));
            }
        }

        // Without --yes we must ask; with no terminal (or JSON for a program) we can't, so refuse instead of hanging.
        var mustRefuse = !options.Yes && !options.DryRun && (options.Json || noTerminal);
        var human = options.Json ? error : output;
        human.WriteLine(options.DryRun ? $"Dry run: what `engram {verb}` would do (worker port {port})" : $"engram {verb} (worker port {port}):");
        foreach (var (folder, _, change, err) in plan)
        {
            human.WriteLine();
            human.WriteLine(folder);
            if (err is not null)
            {
                human.WriteLine($"  SKIP: {err}");
                continue;
            }

            foreach (var action in change!.Actions)
            {
                human.WriteLine($"  - {action}");
            }

            foreach (var warning in change.Warnings)
            {
                human.WriteLine($"  ! {warning}");
            }
        }

        if (options.DryRun || mustRefuse)
        {
            const string refusal = "not changed: no terminal to ask for confirmation; pass --yes";
            foreach (var (folder, of, change, err) in plan)
            {
                if (options.Json)
                {
                    output.WriteLine(Result(folder, of, err is not null ? "skipped" : mustRefuse ? "skipped" : "planned", err ?? (mustRefuse ? refusal : null), change));
                }
            }

            if (mustRefuse)
            {
                error.WriteLine();
                error.WriteLine($"engram {verb}: {refusal}");
                return 1;
            }

            return plan.All(p => p.Error is null) ? 0 : 1;
        }

        if (!options.Yes)
        {
            output.WriteLine();
            output.Write($"{(enable ? "Enable" : "Disable")} Engram in {plan.Count(p => p.Error is null)} folder(s)? [y/N] ");
            var answer = input.ReadLine()?.Trim();
            if (answer is null || !(answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine("Nothing changed.");
                return 0;
            }
        }

        var ok = true;
        human.WriteLine();
        foreach (var (folder, of, _, err) in plan)
        {
            if (err is not null)
            {
                ok = false;
                if (options.Json)
                {
                    output.WriteLine(Result(folder, of, "skipped", err, null));
                }

                continue;
            }

            try
            {
                var change = enable ? enabler.Enable(folder, options.Mcp, dryRun: false) : enabler.Disable(folder, dryRun: false);
                human.WriteLine($"done: {folder}{(change.Warnings.Count > 0 ? $" ({change.Warnings.Count} warning(s) above)" : string.Empty)}");
                if (options.Json)
                {
                    output.WriteLine(Result(folder, of, "done", null, change));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                ok = false;
                human.WriteLine($"FAILED: {folder}: {ex.Message}");
                if (options.Json)
                {
                    output.WriteLine(Result(folder, of, "failed", ex.Message, null));
                }
            }
        }

        if (enable)
        {
            PrintNextSteps(home, getEnv, human);
        }

        return ok ? 0 : 1;
    }

    private static bool IsWslPath(string path) =>
        path.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase) || path.StartsWith("//wsl", StringComparison.OrdinalIgnoreCase);

    private static string Result(string folder, string? worktreeOf, string result, string? reason, ProjectChange? change) => Json(w =>
    {
        w.WriteString("folder", folder);
        w.WriteString("result", result);
        NullableString(w, "reason", reason);
        Strings(w, "actions", change?.Actions ?? []);
        Strings(w, "warnings", change?.Warnings ?? []);
        Strings(w, "files", change?.Files ?? []);
        NullableString(w, "worktree_of", worktreeOf);
    });

    /// <summary>
    /// One compact JSON object (a single line), pure ASCII: non-ASCII is escaped (<c>\uXXXX</c>), so Windows console code pages
    /// can't mangle it when another program reads our stdout.
    /// </summary>
    internal static string Json(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Strings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }

    private static void NullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    private static void PrintNextSteps(string home, Func<string, string?> getEnv, TextWriter output)
    {
        output.WriteLine();
        var token = WorkerToken.Read(home);
        var env = getEnv(WorkerToken.EnvironmentVariable);
        if (token is null)
        {
            output.WriteLine("! No token yet: run `engram install` first, or the hooks will be rejected.");
        }
        else if (env != token)
        {
            output.WriteLine("! ENGRAM_TOKEN isn't set to the current token in this shell. Claude Code must start with it: after `engram install`,");
            output.WriteLine("  restart the app that launches Claude Code (terminals, Chorus, your IDE) so it picks up the new environment.");
        }

        output.WriteLine("Next: start a NEW Claude Code session in each folder. `/hooks` lists Engram's hooks; `engram status` shows the counts grow.");
        output.WriteLine("Undo with `engram disable <folder>`.");
    }
}
