namespace Engram.Worker.Observer;

internal static class ObservationTypes
{
    public static readonly string[] All = ["decision", "bugfix", "feature", "change", "discovery", "gotcha"];

    public static string? Normalize(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is not null && All.Contains(v) ? v : null;
    }
}

/// <summary>One memory the observer extracted from a turn.</summary>
internal sealed record Observation(string Type, string Title, string Narrative, IReadOnlyList<string> Facts, IReadOnlyList<string> Files);

/// <summary>A session summary: the five fields the plan defines.</summary>
internal sealed record SessionSummary(string Request, string Investigated, string Learned, string Completed, string NextSteps);

internal sealed record TurnEvent(long Id, string HookEvent, string? ToolName, string? AgentId, string? ToolInput, string? ToolResponse, string? Text, bool IsError);

/// <summary>Everything the observer sees for one turn, already redacted and truncated by capture.</summary>
internal sealed record TurnInput(
    string SessionId,
    string? PromptId,
    string ProjectName,
    string? Branch,
    string? Prompt,
    IReadOnlyList<TurnEvent> Events,
    string? FinalMessage);

internal sealed record SessionInput(
    string SessionId,
    string ProjectName,
    string? Branch,
    IReadOnlyList<string> Prompts,
    IReadOnlyList<Observation> Observations);

internal sealed record JobRow(long Id, string Kind, string SessionId, string? PromptId, long Attempts);

/// <summary>Token and cost accounting for one provider call.</summary>
internal sealed record LlmUsage(long? InputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheWriteTokens, double? CostUsd);

internal sealed record LlmRequest(string System, string User);

internal sealed record LlmResponse(string Text, LlmUsage Usage, TimeSpan Latency, string Model);

/// <summary>The provider failed (couldn't run, timed out, or reported an error). The job is retried.</summary>
internal sealed class LlmException(string message, TimeSpan? latency = null, Exception? inner = null) : Exception(message, inner)
{
    public TimeSpan? Latency { get; } = latency;
}

/// <summary>A job with nothing to work on (an empty turn). Completed without a provider call.</summary>
internal sealed class NothingToObserveException(string message) : Exception(message);
