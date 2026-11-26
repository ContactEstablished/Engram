namespace Engram.Core.Settings;

/// <summary>
/// Everything in <c>~/.engram/settings.json</c> that Phase 1 uses. Missing keys keep these defaults.
/// Later phases add observer, injection, search and retention sections.
/// </summary>
public sealed record EngramSettings
{
    public WorkerSettings Worker { get; set; } = new();

    public CaptureSettings Capture { get; set; } = new();

    public ObserverSettings Observer { get; set; } = new();

    public InjectionSettings Injection { get; set; } = new();
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

    /// <summary>Longer string values in tool input/output keep their start and end, up to this many UTF-8 bytes.</summary>
    public int MaxStringBytes { get; set; } = 4096;

    /// <summary>Same, for prompts and Claude's final answers: few, short in practice, and the most valuable text.</summary>
    public int MaxPromptBytes { get; set; } = 16384;

    /// <summary>Glob patterns; a tool call touching a matching path is not captured at all.</summary>
    public IReadOnlyList<string> ExcludePaths { get; set; } = ["**/.env*", "**/secrets*.json", "**/*.pfx", "**/*.pem", "**/id_rsa*"];

    /// <summary>Extra regexes whose matches are replaced with <c>&lt;REDACTED&gt;</c>, on top of the built-in rules.</summary>
    public IReadOnlyList<string> RedactPatterns { get; set; } = [];
}

public sealed record ObserverSettings
{
    public const string ClaudeCli = "claude-cli";
    public const string Ollama = "ollama";
    public const string None = "none";

    /// <summary><c>claude-cli</c> (the unmodified <c>claude -p</c>), <c>ollama</c>, or <c>none</c> (capture only).</summary>
    public string Provider { get; set; } = ClaudeCli;

    public string Model { get; set; } = "haiku";

    /// <summary>Hard cap on provider calls (including retries) in any rolling hour.</summary>
    public int MaxCallsPerHour { get; set; } = 120;

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Upper bound on the text sent to the summarizer for one turn; tool output shrinks to fit.</summary>
    public int MaxInputChars { get; set; } = 24000;

    /// <summary>Path to <c>claude</c> when it isn't on PATH.</summary>
    public string? ClaudePath { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("ollama")]
    public OllamaSettings OllamaOptions { get; set; } = new();
}

public sealed record OllamaSettings
{
    public string Url { get; set; } = "http://127.0.0.1:11434";

    public string Model { get; set; } = "qwen3:14b";
}

/// <summary>What SessionStart puts in front of Claude: the last summary and a ranked index of observations.</summary>
public sealed record InjectionSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Claude Code shows only a 2,000-character preview past 10,000; stay well under.</summary>
    public int MaxChars { get; set; } = 6000;

    public int MaxRows { get; set; } = 25;

    /// <summary>Rank multiplier per observation type; unlisted types weigh 1.</summary>
    public Dictionary<string, double> TypeWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["decision"] = 3,
        ["gotcha"] = 3,
        ["bugfix"] = 2,
        ["feature"] = 1,
        ["change"] = 1,
        ["discovery"] = 1,
    };

    /// <summary>Age (days) at which an observation's recency factor halves.</summary>
    public double HalfLifeDays { get; set; } = 21;

    /// <summary>Rank multiplier for observations made on the current branch.</summary>
    public double SameBranchBoost { get; set; } = 1.5;
}
