using Engram.Cli.Projects;
using Engram.Core;
using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Cli.Commands;

/// <summary>
/// <c>engram enable [FOLDER...]</c> and <c>engram disable [FOLDER...]</c>: switch Engram on or off for project
/// folders. Shows what will change and asks first (<c>--yes</c> skips the question, <c>--dry-run</c> only shows it).
/// </summary>
internal static class EnableCommand
{
    internal sealed record Options(IReadOnlyList<string> Folders, bool Worktrees, McpMode Mcp, bool Yes, bool DryRun);

    public static int Run(bool enable, string[] args, Func<string, string?> getEnv, TextWriter output, TextReader input) =>
        Run(enable, Parse(args), getEnv, output, input, new SystemProjectTools(), TimeProvider.System);

    internal static Options Parse(string[] args)
    {
        var folders = new List<string>();
        bool worktrees = false, yes = false, dry = false;
        var mcp = McpMode.Local;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--worktrees" or "-w": worktrees = true; break;
                case "--yes" or "-y": yes = true; break;
                case "--dry-run": dry = true; break;
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

        return new Options(folders.Count == 0 ? ["."] : folders, worktrees, mcp, yes, dry);
    }

    internal static int Run(bool enable, Options options, Func<string, string?> getEnv, TextWriter output, TextReader input, IProjectTools tools, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        var home = EngramPaths.Home(getEnv);
        var (settings, _) = SettingsLoader.Load(home, getEnv);
        var enabler = new ProjectEnabler(tools, home, settings.Worker.Port, settings.Capture.ToolMatcher, time);
        var verb = enable ? "enable" : "disable";

        var folders = new List<string>();
        foreach (var folder in options.Folders)
        {
            var full = Path.GetFullPath(folder);
            foreach (var f in options.Worktrees ? enabler.ExpandWorktrees(full) : [full])
            {
                if (!folders.Contains(f, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                {
                    folders.Add(f);
                }
            }
        }

        // Plan first (a dry run of every folder), so nothing changes before you've seen it.
        var plan = new List<(string Folder, ProjectChange? Change, string? Error)>();
        foreach (var folder in folders)
        {
            try
            {
                plan.Add((folder, enable ? enabler.Enable(folder, options.Mcp, dryRun: true) : enabler.Disable(folder, dryRun: true), null));
            }
            catch (ArgumentException ex)
            {
                plan.Add((folder, null, ex.Message));
            }
        }

        output.WriteLine(options.DryRun ? $"Dry run: what `engram {verb}` would do (worker port {settings.Worker.Port})" : $"engram {verb} (worker port {settings.Worker.Port}):");
        foreach (var (folder, change, error) in plan)
        {
            output.WriteLine();
            output.WriteLine(folder);
            if (error is not null)
            {
                output.WriteLine($"  SKIP: {error}");
                continue;
            }

            foreach (var action in change!.Actions)
            {
                output.WriteLine($"  - {action}");
            }

            foreach (var warning in change.Warnings)
            {
                output.WriteLine($"  ! {warning}");
            }
        }

        var doable = plan.Where(p => p.Error is null).Select(p => p.Folder).ToList();
        if (options.DryRun || doable.Count == 0)
        {
            return doable.Count == 0 && plan.Count > 0 ? 1 : 0;
        }

        if (!options.Yes)
        {
            output.WriteLine();
            output.Write($"{(enable ? "Enable" : "Disable")} Engram in {doable.Count} folder(s)? [y/N] ");
            var answer = input.ReadLine()?.Trim();
            if (answer is null || !(answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine("Nothing changed.");
                return 0;
            }
        }

        var failed = 0;
        output.WriteLine();
        foreach (var folder in doable)
        {
            try
            {
                var change = enable ? enabler.Enable(folder, options.Mcp, dryRun: false) : enabler.Disable(folder, dryRun: false);
                output.WriteLine($"done: {folder}{(change.Warnings.Count > 0 ? $" ({change.Warnings.Count} warning(s) above)" : string.Empty)}");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                failed++;
                output.WriteLine($"FAILED: {folder}: {ex.Message}");
            }
        }

        if (enable)
        {
            PrintNextSteps(home, getEnv, output);
        }

        return failed == 0 ? 0 : 1;
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
