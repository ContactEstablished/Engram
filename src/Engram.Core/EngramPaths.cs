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
}
