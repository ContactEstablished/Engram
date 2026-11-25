using System.Text.Json;
using System.Text.Json.Serialization;

namespace Engram.Core.Hooks;

// Phase 0 draft models, written from the hooks reference and checked against real captures by the
// contract tests. "required" marks fields Engram depends on: a capture missing one fails the tests.
// Everything else is optional. Unknown fields land in ExtensionData, so new Claude Code fields show up
// without breaking deserialization. tool_input / tool_response stay JsonElement until Phase 0 records
// their shapes per tool.

/// <summary>Fields common to every hook event.</summary>
public abstract record HookPayload
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("transcript_path")]
    public required string TranscriptPath { get; init; }

    [JsonPropertyName("cwd")]
    public required string Cwd { get; init; }

    [JsonPropertyName("hook_event_name")]
    public required string HookEventName { get; init; }

    /// <summary>Absent until the first user input of a session.</summary>
    [JsonPropertyName("prompt_id")]
    public string? PromptId { get; init; }

    /// <summary>Not sent on every event.</summary>
    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; init; }

    [JsonPropertyName("scratchpad_dir")]
    public string? ScratchpadDir { get; init; }

    /// <summary>Documented as an object with <c>level</c>; kept raw until Phase 0 confirms it.</summary>
    [JsonPropertyName("effort")]
    public JsonElement? Effort { get; init; }

    /// <summary>Present when the hook fires inside a subagent or teammate.</summary>
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("agent_type")]
    public string? AgentType { get; init; }

    /// <summary>Fields the model doesn't declare. Contract tests report them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record SessionStartPayload : HookPayload
{
    /// <summary>startup | resume | clear | compact | fork.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("session_title")]
    public string? SessionTitle { get; init; }

    [JsonPropertyName("seconds_since_last_response")]
    public double? SecondsSinceLastResponse { get; init; }

    [JsonPropertyName("context_tokens")]
    public long? ContextTokens { get; init; }

    [JsonPropertyName("prompt_cache_likely_expired")]
    public bool? PromptCacheLikelyExpired { get; init; }

    [JsonPropertyName("estimated_cache_write_usd")]
    public double? EstimatedCacheWriteUsd { get; init; }
}

public sealed record UserPromptSubmitPayload : HookPayload
{
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("session_title")]
    public string? SessionTitle { get; init; }
}

public sealed record PreToolUsePayload : HookPayload
{
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public required JsonElement ToolInput { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("mcp_server")]
    public JsonElement? McpServer { get; init; }
}

public sealed record PostToolUsePayload : HookPayload
{
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public required JsonElement ToolInput { get; init; }

    /// <summary>Structured tool output (an error string for failed calls).</summary>
    [JsonPropertyName("tool_response")]
    public required JsonElement ToolResponse { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("duration_ms")]
    public double? DurationMs { get; init; }

    [JsonPropertyName("mcp_server")]
    public JsonElement? McpServer { get; init; }
}

public sealed record PostToolUseFailurePayload : HookPayload
{
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public required JsonElement ToolInput { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("error")]
    public required string Error { get; init; }

    [JsonPropertyName("is_interrupt")]
    public bool? IsInterrupt { get; init; }

    [JsonPropertyName("duration_ms")]
    public double? DurationMs { get; init; }

    [JsonPropertyName("mcp_server")]
    public JsonElement? McpServer { get; init; }
}

public sealed record PostToolBatchPayload : HookPayload
{
    [JsonPropertyName("tool_calls")]
    public required IReadOnlyList<ToolBatchCall> ToolCalls { get; init; }
}

public sealed record ToolBatchCall
{
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public required JsonElement ToolInput { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    /// <summary>Documented as the serialized tool_result text (a string), unlike PostToolUse.</summary>
    [JsonPropertyName("tool_response")]
    public JsonElement? ToolResponse { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record StopPayload : HookPayload
{
    [JsonPropertyName("stop_hook_active")]
    public required bool StopHookActive { get; init; }

    [JsonPropertyName("last_assistant_message")]
    public string? LastAssistantMessage { get; init; }

    [JsonPropertyName("background_tasks")]
    public JsonElement? BackgroundTasks { get; init; }

    [JsonPropertyName("session_crons")]
    public JsonElement? SessionCrons { get; init; }
}

public sealed record SubagentStopPayload : HookPayload
{
    [JsonPropertyName("stop_hook_active")]
    public required bool StopHookActive { get; init; }

    [JsonPropertyName("agent_transcript_path")]
    public string? AgentTranscriptPath { get; init; }

    [JsonPropertyName("last_assistant_message")]
    public string? LastAssistantMessage { get; init; }

    [JsonPropertyName("background_tasks")]
    public JsonElement? BackgroundTasks { get; init; }

    [JsonPropertyName("session_crons")]
    public JsonElement? SessionCrons { get; init; }
}

public sealed record PreCompactPayload : HookPayload
{
    /// <summary>manual | auto.</summary>
    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    [JsonPropertyName("custom_instructions")]
    public string? CustomInstructions { get; init; }
}

public sealed record PostCompactPayload : HookPayload
{
    /// <summary>manual | auto.</summary>
    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    [JsonPropertyName("compact_summary")]
    public string? CompactSummary { get; init; }
}

public sealed record SessionEndPayload : HookPayload
{
    /// <summary>clear | resume | logout | prompt_input_exit | other.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>Any event without a typed model. Only the common fields are checked.</summary>
public sealed record UnknownHookPayload : HookPayload;
