namespace Engram.Core.Hooks;

/// <summary>Claude Code hook event names (exact casing from https://code.claude.com/docs/en/hooks).</summary>
public static class HookEvents
{
    public const string SessionStart = "SessionStart";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string PreToolUse = "PreToolUse";
    public const string PostToolUse = "PostToolUse";
    public const string PostToolUseFailure = "PostToolUseFailure";
    public const string PostToolBatch = "PostToolBatch";
    public const string Stop = "Stop";
    public const string SubagentStop = "SubagentStop";
    public const string PreCompact = "PreCompact";
    public const string PostCompact = "PostCompact";
    public const string SessionEnd = "SessionEnd";

    /// <summary>The events registered by the Phase 0 spike, each with a typed model.</summary>
    public static IReadOnlyList<string> Phase0 { get; } =
    [
        SessionStart, UserPromptSubmit, PreToolUse, PostToolUse, PostToolUseFailure, PostToolBatch,
        Stop, SubagentStop, PreCompact, PostCompact, SessionEnd,
    ];

    /// <summary>Events whose payload carries a single <c>tool_name</c>.</summary>
    public static bool IsToolEvent(string eventName) =>
        eventName is PreToolUse or PostToolUse or PostToolUseFailure;
}
