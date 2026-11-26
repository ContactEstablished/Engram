using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Engram.Worker.Storage;

internal sealed record SessionUpsert(
    string Id,
    long? ProjectId,
    string Platform,
    string Cwd,
    string? Worktree,
    string? Branch,
    string? Model,
    string? Source,
    string? TranscriptPath,
    DateTimeOffset At);

internal sealed record EventInsert(
    string SessionId,
    string? PromptId,
    string HookEvent,
    string? ToolName,
    string? ToolUseId,
    string? AgentId,
    string? ToolInput,
    string? ToolResponse,
    string? Text,
    bool IsError,
    long? DurationMs,
    DateTimeOffset At);

internal static class JobKinds
{
    public const string ObserveTurn = "observe-turn";
    public const string SummarizeCheckpoint = "summarize-checkpoint";
    public const string SummarizeSession = "summarize-session";
}

/// <summary>Writes for the capture pipeline. Every method is idempotent for hook retries.</summary>
internal sealed class CaptureStore(Database database)
{
    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public long GetOrCreateProject(string identity, string name, DateTimeOffset at)
    {
        using var c = database.Open();
        c.Execute(
            "INSERT INTO projects (identity, name, created_at) VALUES (@identity, @name, @at) ON CONFLICT(identity) DO NOTHING",
            new { identity, name, at = Iso(at) });
        return c.ExecuteScalar<long>("SELECT id FROM projects WHERE identity = @identity", new { identity });
    }

    public void UpsertSession(SessionUpsert s)
    {
        ArgumentNullException.ThrowIfNull(s);
        using var c = database.Open();
        c.Execute(
            """
            INSERT INTO sessions (id, project_id, platform, cwd, worktree, branch, model, source, transcript_path, started_at, last_seen_at)
            VALUES (@Id, @ProjectId, @Platform, @Cwd, @Worktree, @Branch, @Model, @Source, @TranscriptPath, @At, @At)
            ON CONFLICT(id) DO UPDATE SET
                project_id      = coalesce(sessions.project_id, excluded.project_id),
                worktree        = coalesce(excluded.worktree, sessions.worktree),
                branch          = coalesce(excluded.branch, sessions.branch),
                model           = coalesce(excluded.model, sessions.model),
                source          = coalesce(excluded.source, sessions.source),
                transcript_path = coalesce(excluded.transcript_path, sessions.transcript_path),
                last_seen_at    = excluded.last_seen_at,
                ended_at        = CASE WHEN excluded.source IS NOT NULL THEN NULL ELSE sessions.ended_at END,
                end_reason      = CASE WHEN excluded.source IS NOT NULL THEN NULL ELSE sessions.end_reason END
            """,
            new { s.Id, s.ProjectId, s.Platform, s.Cwd, s.Worktree, s.Branch, s.Model, s.Source, s.TranscriptPath, At = Iso(s.At) });
    }

    public bool SessionExists(string sessionId)
    {
        using var c = database.Open();
        return c.ExecuteScalar<long>("SELECT count(*) FROM sessions WHERE id = @sessionId", new { sessionId }) > 0;
    }

    public void EndSession(string sessionId, string? reason, DateTimeOffset at)
    {
        using var c = database.Open();
        c.Execute(
            "UPDATE sessions SET ended_at = @at, end_reason = @reason, last_seen_at = @at WHERE id = @sessionId",
            new { sessionId, reason, at = Iso(at) });
    }

    public void InsertPrompt(string sessionId, string? promptId, string text, DateTimeOffset at)
    {
        using var c = database.Open();
        c.Execute(
            "INSERT INTO prompts (session_id, prompt_id, text, created_at) VALUES (@sessionId, @promptId, @text, @at) ON CONFLICT DO NOTHING",
            new { sessionId, promptId, text, at = Iso(at) });
    }

    public void InsertEvent(EventInsert e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var c = database.Open();
        c.Execute(
            """
            INSERT INTO events (session_id, prompt_id, hook_event, tool_name, tool_use_id, agent_id, tool_input, tool_response, text, is_error, duration_ms, created_at)
            VALUES (@SessionId, @PromptId, @HookEvent, @ToolName, @ToolUseId, @AgentId, @ToolInput, @ToolResponse, @Text, @IsError, @DurationMs, @At)
            ON CONFLICT DO NOTHING
            """,
            new { e.SessionId, e.PromptId, e.HookEvent, e.ToolName, e.ToolUseId, e.AgentId, e.ToolInput, e.ToolResponse, e.Text, e.IsError, e.DurationMs, At = Iso(e.At) });
    }

    /// <summary>
    /// Queues work for the observer. A retried hook (same kind, session, prompt) is ignored; a summary job that
    /// already ran is queued again, so a resumed session gets an updated summary.
    /// </summary>
    public void EnqueueJob(string kind, string sessionId, string? promptId, DateTimeOffset at)
    {
        using var c = database.Open();
        c.Execute(
            """
            INSERT INTO jobs (kind, session_id, prompt_id, next_run_at, created_at, updated_at)
            VALUES (@kind, @sessionId, @promptId, @at, @at, @at)
            ON CONFLICT (kind, session_id, ifnull(prompt_id, '')) DO UPDATE SET
                status = 'pending', attempts = 0, next_run_at = @at, last_error = NULL, updated_at = @at
            WHERE jobs.kind <> 'observe-turn' AND jobs.status IN ('done', 'parked')
            """,
            new { kind, sessionId, promptId, at = Iso(at) });
    }

    internal SqliteConnection OpenForTests() => database.Open();
}
