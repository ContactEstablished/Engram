using Engram.Core;
using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Cli.Commands;

/// <summary>
/// <c>engram install</c>: creates <c>~/.engram</c>, default settings and the worker token, and makes the
/// token visible to Claude Code as <c>ENGRAM_TOKEN</c>. Asks before changing anything outside
/// <c>~/.engram</c> (the user environment on Windows, <c>~/.profile</c> on Linux) unless <c>--yes</c>.
/// Never touches Claude Code's own settings.
/// </summary>
internal static class InstallCommand
{
    internal const string ProfileMarker = "# engram: export the worker token for Claude Code hooks";

    internal sealed record Options(bool Yes, bool RotateToken, bool SkipEnvironment);

    public static int Run(string[] args) => Run(Parse(args), Environment.GetEnvironmentVariable, Console.Out, Console.In);

    internal static Options Parse(string[] args)
    {
        bool yes = false, rotate = false, skipEnv = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--yes" or "-y": yes = true; break;
                case "--rotate-token": rotate = true; break;
                case "--no-env": skipEnv = true; break;
                default: throw new ArgumentException($"install: unknown option '{arg}'");
            }
        }

        return new Options(yes, rotate, skipEnv);
    }

    internal static int Run(Options options, Func<string, string?> getEnv, TextWriter output, TextReader input, Action<string>? setUserToken = null, string? profilePath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        var home = EngramPaths.Home(getEnv);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(EngramPaths.Logs(home));
        output.WriteLine($"Engram home: {home}");

        var settingsFile = EngramPaths.SettingsFile(home);
        if (File.Exists(settingsFile))
        {
            output.WriteLine($"  settings:  kept {settingsFile}");
        }
        else
        {
            File.WriteAllText(settingsFile, "// Engram settings. Missing keys use built-in defaults; ENGRAM_* environment variables override.\n" + SettingsLoader.Serialize(new EngramSettings()) + "\n");
            output.WriteLine($"  settings:  wrote defaults to {settingsFile}");
        }

        var hadToken = WorkerToken.Read(home) is not null;
        var token = WorkerToken.ReadOrCreate(home, options.RotateToken);
        output.WriteLine($"  token:     {(options.RotateToken ? "rotated" : hadToken ? "kept" : "created")} ({EngramPaths.TokenFile(home)})");

        if (options.SkipEnvironment)
        {
            output.WriteLine("  ENGRAM_TOKEN: skipped (--no-env). HTTP hooks need it in Claude Code's environment.");
        }
        else if (OperatingSystem.IsWindows())
        {
            if (getEnv(WorkerToken.EnvironmentVariable) == token)
            {
                output.WriteLine("  ENGRAM_TOKEN: already set");
            }
            else if (Confirm(options, output, input, "Set the user environment variable ENGRAM_TOKEN (needed by the HTTP hooks)?"))
            {
                (setUserToken ?? (t => Environment.SetEnvironmentVariable(WorkerToken.EnvironmentVariable, t, EnvironmentVariableTarget.User)))(token);
                output.WriteLine("  ENGRAM_TOKEN: set for your user. Restart terminals and Claude Code to pick it up.");
            }
            else
            {
                output.WriteLine("  ENGRAM_TOKEN: not set. HTTP hooks will be rejected until it is.");
            }
        }
        else
        {
            var profile = profilePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".profile");
            var line = $"{ProfileMarker}\n[ -f \"$HOME/.engram/token\" ] && export ENGRAM_TOKEN=\"$(cat \"$HOME/.engram/token\")\"\n";
            if (File.Exists(profile) && File.ReadAllText(profile).Contains(ProfileMarker, StringComparison.Ordinal))
            {
                output.WriteLine($"  ENGRAM_TOKEN: already exported by {profile}");
            }
            else if (Confirm(options, output, input, $"Add a line to {profile} that exports ENGRAM_TOKEN from ~/.engram/token?"))
            {
                File.AppendAllText(profile, "\n" + line);
                output.WriteLine($"  ENGRAM_TOKEN: added to {profile}. Open a new login shell to pick it up.");
            }
            else
            {
                output.WriteLine($"  ENGRAM_TOKEN: not exported. Add this to your shell profile:\n{line}");
            }
        }

        output.WriteLine();
        output.WriteLine("Next: add the hooks (see hooks/README.md), then run `engram status`.");
        return 0;
    }

    private static bool Confirm(Options options, TextWriter output, TextReader input, string question)
    {
        if (options.Yes)
        {
            return true;
        }

        output.Write($"{question} [y/N] ");
        var answer = input.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
