namespace Engram.Core;

/// <summary>Resolves Engram's data folder. <c>ENGRAM_HOME</c> overrides the default <c>~/.engram</c>.</summary>
public static class EngramPaths
{
    public const string HomeVariable = "ENGRAM_HOME";

    public static string Home(Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        var overridden = getEnv(HomeVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        // %USERPROFILE% on Windows, $HOME on Linux/WSL.
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".engram");
    }

    public static string SpikeDir(Func<string, string?> getEnv) => Path.Combine(Home(getEnv), "spike");

    /// <summary>User settings (JSON with comments).</summary>
    public static string SettingsFile(string home) => Path.Combine(home, "settings.json");

    /// <summary>The worker's bearer token. Created by <c>engram install</c> or by the worker on first start.</summary>
    public static string TokenFile(string home) => Path.Combine(home, "token");

    public static string Database(string home) => Path.Combine(home, "engram.db");

    public static string Logs(string home) => Path.Combine(home, "logs");

    public static string Backups(string home) => Path.Combine(home, "backups");

    /// <summary>Hook payloads that couldn't reach the worker; the worker ingests them when it starts.</summary>
    public static string Spool(string home) => Path.Combine(home, "spool");

    /// <summary>Working folder for summarizer runs. Events whose cwd is inside it are dropped.</summary>
    public static string ObserverSandbox(string home) => Path.Combine(home, "observer-sandbox");

    /// <summary>Held open by the running worker so only one instance runs per data folder.</summary>
    public static string WorkerLock(string home) => Path.Combine(home, "worker.lock");
}
