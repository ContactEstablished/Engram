using Engram.Core.Settings;

namespace Engram.Worker;

/// <summary>Resolved once at startup: where data lives, the settings, and the hook token.</summary>
internal sealed record WorkerContext(string Home, EngramSettings Settings, string Token, string Platform)
{
    public static string DetectPlatform(Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (!string.IsNullOrEmpty(getEnv("WSL_DISTRO_NAME")))
        {
            return "wsl";
        }

        try
        {
            return File.Exists("/proc/version") && File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase)
                ? "wsl"
                : "linux";
        }
        catch (IOException)
        {
            return "linux";
        }
    }
}
