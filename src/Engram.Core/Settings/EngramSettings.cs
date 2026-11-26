namespace Engram.Core.Settings;

/// <summary>
/// Everything in <c>~/.engram/settings.json</c> that Phase 1 uses. Missing keys keep these defaults.
/// Later phases add observer, injection, search and retention sections.
/// </summary>
public sealed record EngramSettings
{
    public WorkerSettings Worker { get; set; } = new();

    public CaptureSettings Capture { get; set; } = new();
}

public sealed record WorkerSettings
{
    public const int DefaultPort = 47613;

    public int Port { get; set; } = DefaultPort;

    /// <summary>Stop after this many minutes without a request. 0 keeps the worker running.</summary>
    public int IdleShutdownMinutes { get; set; } = 60;
}

public sealed record CaptureSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Regex over tool names to keep (anchored). Reads, globs and greps are skipped by default.</summary>
    public string ToolMatcher { get; set; } = "Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell|WebFetch|Agent|Task";

    /// <summary>Tools never captured, even if <see cref="ToolMatcher"/> matches them (Phase 0 §11).</summary>
    public IReadOnlyList<string> SkipTools { get; set; } = ["SubagentHandback", "ToolSearch"];

    /// <summary>Longer string values in tool input/output are truncated to this many UTF-8 bytes.</summary>
    public int MaxStringBytes { get; set; } = 4096;

    /// <summary>Glob patterns; a tool call touching a matching path is not captured at all.</summary>
    public IReadOnlyList<string> ExcludePaths { get; set; } = ["**/.env*", "**/secrets*.json", "**/*.pfx", "**/*.pem", "**/id_rsa*"];

    /// <summary>Extra regexes whose matches are replaced with <c>&lt;REDACTED&gt;</c>, on top of the built-in rules.</summary>
    public IReadOnlyList<string> RedactPatterns { get; set; } = [];
}
