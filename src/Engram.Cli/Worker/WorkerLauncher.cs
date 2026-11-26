using System.Diagnostics;

namespace Engram.Cli.Worker;

/// <summary>
/// Starts <c>engram-worker</c> fully detached from the hook process: it must not inherit the hook's
/// stdout (Claude Code would wait for it to close) and must outlive the hook.
/// </summary>
internal static class WorkerLauncher
{
    public const string PathVariable = "ENGRAM_WORKER_PATH";

    /// <summary>The worker binary: <c>ENGRAM_WORKER_PATH</c>, else next to <c>engram</c>.</summary>
    public static string? Locate(Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        if (getEnv(PathVariable) is { Length: > 0 } explicitPath)
        {
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        var name = OperatingSystem.IsWindows() ? "engram-worker.exe" : "engram-worker";
        var besideCli = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(besideCli) ? besideCli : null;
    }

    /// <summary>Starts the worker; returns false (with a reason) if it couldn't be started.</summary>
    public static bool TryStart(string workerPath, string home, out string? error)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                // ShellExecute doesn't inherit our handles; the console window stays hidden.
                psi = new ProcessStartInfo(workerPath)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = home,
                };
            }
            else
            {
                // A new session (setsid) so the worker survives the hook's process group; all std streams
                // to /dev/null so nothing holds Claude Code's pipe open.
                psi = new ProcessStartInfo("/bin/sh")
                {
                    UseShellExecute = false,
                    WorkingDirectory = home,
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("""(command -v setsid >/dev/null && setsid "$0" || "$0") </dev/null >/dev/null 2>&1 &""");
                psi.ArgumentList.Add(workerPath);
            }

            Directory.CreateDirectory(home);
            using var process = Process.Start(psi);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }
}
