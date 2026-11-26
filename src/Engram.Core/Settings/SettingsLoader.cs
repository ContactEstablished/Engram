using System.Globalization;
using System.Text.Json;

namespace Engram.Core.Settings;

/// <summary>
/// Loads settings with precedence (highest first): <c>ENGRAM_*</c> environment variables, the user file,
/// built-in defaults. A broken file never stops capture: it falls back to defaults and reports a warning.
/// </summary>
public static class SettingsLoader
{
    public const string RepoFileName = ".engram.json";
    public const string PortVariable = "ENGRAM_PORT";

    public static (EngramSettings Settings, IReadOnlyList<string> Warnings) Load(string home, Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        var warnings = new List<string>();
        var settings = new EngramSettings();
        var file = EngramPaths.SettingsFile(home);
        if (File.Exists(file))
        {
            try
            {
                settings = Parse(File.ReadAllBytes(file)) ?? settings;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{file}: {ex.Message} Using defaults.");
            }
        }

        if (int.TryParse(getEnv(PortVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
        {
            settings = settings with { Worker = settings.Worker with { Port = port } };
        }

        return (Normalize(settings, warnings), warnings);
    }

    public static EngramSettings? Parse(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, SettingsJsonContext.Default.EngramSettings);

    public static string Serialize(EngramSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.EngramSettings);

    /// <summary>Reads <c>.engram.json</c> at <paramref name="repoRoot"/>; null if absent or unreadable.</summary>
    public static RepoSettings? LoadRepo(string repoRoot)
    {
        var file = Path.Combine(repoRoot, RepoFileName);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllBytes(file), SettingsJsonContext.Default.RepoSettings) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static EngramSettings Normalize(EngramSettings s, List<string> warnings)
    {
        var capture = s.Capture;
        if (capture.MaxStringBytes < 256)
        {
            warnings.Add("capture.maxStringBytes below 256; using 256.");
            capture = capture with { MaxStringBytes = 256 };
        }

        var worker = s.Worker;
        if (worker.Port is <= 0 or >= 65536)
        {
            warnings.Add($"worker.port {worker.Port} is invalid; using {WorkerSettings.DefaultPort}.");
            worker = worker with { Port = WorkerSettings.DefaultPort };
        }

        // JSON null for a list leaves the property null despite the annotation; restore defaults.
        var defaults = new CaptureSettings();
        capture = capture with
        {
            ToolMatcher = string.IsNullOrWhiteSpace(capture.ToolMatcher) ? defaults.ToolMatcher : capture.ToolMatcher,
            SkipTools = capture.SkipTools ?? defaults.SkipTools,
            ExcludePaths = capture.ExcludePaths ?? defaults.ExcludePaths,
            RedactPatterns = capture.RedactPatterns ?? defaults.RedactPatterns,
        };
        return s with { Capture = capture, Worker = worker };
    }
}
