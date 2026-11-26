using System.Text.Json.Serialization;

namespace Engram.Core.Settings;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(EngramSettings))]
[JsonSerializable(typeof(RepoSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// A repo's <c>.engram.json</c>. Repo files can only tighten settings (Phase 1: turn capture off), so a
/// cloned repo can't redirect or widen what Engram records.
/// </summary>
public sealed record RepoSettings
{
    public RepoCaptureSettings? Capture { get; set; }
}

public sealed record RepoCaptureSettings
{
    public bool? Enabled { get; set; }
}
