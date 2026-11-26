using System.Globalization;
using System.Text.Json;
using Dapper;
using Engram.Worker.Storage;

namespace Engram.Worker.Observer;

/// <summary>Reads and writes for the observer: the job queue, turn input, observations, summaries, call log.</summary>
internal sealed class ObserverStore(Database database, TimeProvider time)
{
    public const int MaxAttempts = 3;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    internal static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private string Now() => Iso(time.GetUtcNow());

    /// <summary>Jobs that were running when the worker last stopped go back to pending.</summary>
    public int ResetRunningJobs()
    {
        using var c = database.Open();
        return c.Execute("UPDATE jobs SET status = 'pending', updated_at = @now WHERE status = 'running'", new { now = Now() });
    }

    /// <summary>
    /// Takes the oldest due job. Summaries wait for the session's turn jobs, so they see its observations.
    /// </summary>
    public JobRow? ClaimNextJob()
    {
        using var c = database.Open();
        return c.QuerySingleOrDefault<JobRow>(
            """
            UPDATE jobs SET status = 'running', updated_at = @now
            WHERE id = (
                SELECT id FROM jobs
                WHERE status = 'pending' AND next_run_at <= @now
                  AND (kind = 'observe-turn' OR NOT EXISTS (
                        SELECT 1 FROM jobs j2
                        WHERE j2.session_id = jobs.session_id AND j2.kind = 'observe-turn' AND j2.status IN ('pending', 'running')))
                ORDER BY next_run_at, id
                LIMIT 1)
            RETURNING id AS Id, kind AS Kind, session_id AS SessionId, prompt_id AS PromptId, attempts AS Attempts
            """,
            new { now = Now() });
    }

    /// <summary>Records a failure: retry with backoff, or park the job after <see cref="MaxAttempts"/>.</summary>
    public string Fail(JobRow job, string error)
    {
        var attempts = job.Attempts + 1;
        var parked = attempts >= MaxAttempts;
        var now = time.GetUtcNow();
        using var c = database.Open();
        c.Execute(
            "UPDATE jobs SET status = @status, attempts = @attempts, next_run_at = @next, last_error = @error, updated_at = @now WHERE id = @id",
            new
            {
                id = job.Id,
                status = parked ? "parked" : "pending",
                attempts,
                next = Iso(now + Backoff[(int)Math.Min(attempts - 1, Backoff.Length - 1)]),
                error = error.Length > 500 ? error[..500] : error,
                now = Iso(now),
            });
        return parked ? "parked" : "retry";
    }

    public void Complete(JobRow job, string? note = null)
    {
        using var c = database.Open();
        c.Execute("UPDATE jobs SET status = 'done', last_error = @note, updated_at = @now WHERE id = @id", new { id = job.Id, note, now = Now() });
    }

    /// <summary>Jobs waiting or running; parked jobs don't count (they need attention, not the worker).</summary>
    public long OpenJobCount()
    {
        using var c = database.Open();
        return c.ExecuteScalar<long>("SELECT count(*) FROM jobs WHERE status IN ('pending', 'running')");
    }

    public long CallsInLastHour()
    {
        using var c = database.Open();
        return c.ExecuteScalar<long>("SELECT count(*) FROM llm_calls WHERE created_at >= @since", new { since = Iso(time.GetUtcNow().AddHours(-1)) });
    }

    public void LogCall(long? jobId, string provider, string model, bool ok, string? error, LlmUsage? usage, TimeSpan latency)
    {
        using var c = database.Open();
        c.Execute(
            """
            INSERT INTO llm_calls (job_id, provider, model, ok, error, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd, latency_ms, created_at)
            VALUES (@jobId, @provider, @model, @ok, @error, @inTokens, @outTokens, @cacheRead, @cacheWrite, @cost, @latency, @now)
            """,
            new
            {
                jobId,
                provider,
                model,
                ok,
                error = error is { Length: > 300 } ? error[..300] : error,
                inTokens = usage?.InputTokens,
                outTokens = usage?.OutputTokens,
                cacheRead = usage?.CacheReadTokens,
                cacheWrite = usage?.CacheWriteTokens,
                cost = usage?.CostUsd,
                latency = (long)latency.TotalMilliseconds,
                now = Now(),
            });
    }

    // ---- turn and session input --------------------------------------------------------------------

    public TurnInput LoadTurn(JobRow job)
    {
        using var c = database.Open();
        var session = c.QuerySingleOrDefault<(string? Name, string? Branch)>(
            "SELECT p.name AS Name, s.branch AS Branch FROM sessions s LEFT JOIN projects p ON p.id = s.project_id WHERE s.id = @id",
            new { id = job.SessionId });
        var prompt = c.QueryFirstOrDefault<string>("SELECT text FROM prompts WHERE session_id = @s AND prompt_id IS @p", new { s = job.SessionId, p = job.PromptId });
        var events = c.Query<(long Id, string HookEvent, string? ToolName, string? AgentId, string? ToolInput, string? ToolResponse, string? Text, long IsError)>(
            """
            SELECT id AS Id, hook_event AS HookEvent, tool_name AS ToolName, agent_id AS AgentId, tool_input AS ToolInput,
                   tool_response AS ToolResponse, text AS Text, is_error AS IsError
            FROM events WHERE session_id = @s AND prompt_id IS @p ORDER BY id
            """,
            new { s = job.SessionId, p = job.PromptId })
            .Select(e => new TurnEvent(e.Id, e.HookEvent, e.ToolName, e.AgentId, e.ToolInput, e.ToolResponse, e.Text, e.IsError != 0))
            .ToList();
        var final = events.LastOrDefault(e => e.HookEvent == "Stop")?.Text;
        events.RemoveAll(e => e.HookEvent == "Stop");
        return new TurnInput(job.SessionId, job.PromptId, session.Name ?? "unknown", session.Branch, prompt, events, final);
    }

    public SessionInput LoadSession(string sessionId)
    {
        using var c = database.Open();
        var session = c.QuerySingleOrDefault<(string? Name, string? Branch)>(
            "SELECT p.name AS Name, s.branch AS Branch FROM sessions s LEFT JOIN projects p ON p.id = s.project_id WHERE s.id = @sessionId",
            new { sessionId });
        var prompts = c.Query<string>("SELECT text FROM prompts WHERE session_id = @sessionId ORDER BY id", new { sessionId }).ToList();
        var observations = c.Query<(string Type, string Title, string Narrative, string Facts, string Files)>(
                "SELECT type, title, narrative, facts, files FROM observations WHERE session_id = @sessionId ORDER BY id", new { sessionId })
            .Select(o => new Observation(o.Type, o.Title, o.Narrative, ParseList(o.Facts), ParseList(o.Files)))
            .ToList();
        return new SessionInput(sessionId, session.Name ?? "unknown", session.Branch, prompts, observations);
    }

    // ---- results --------------------------------------------------------------------------------------

    /// <summary>Stores the turn's observations and completes the job in one transaction (no duplicates after a crash).</summary>
    public int SaveObservations(JobRow job, IReadOnlyList<Observation> observations, string provider, string model, IReadOnlyList<long> sourceEventIds)
    {
        using var c = database.Open();
        using var tx = c.BeginTransaction();
        var meta = c.QuerySingleOrDefault<(long? ProjectId, string? Branch)>(
            "SELECT project_id AS ProjectId, branch AS Branch FROM sessions WHERE id = @id", new { id = job.SessionId }, tx);
        var now = Now();
        foreach (var o in observations)
        {
            c.Execute(
                """
                INSERT INTO observations (session_id, project_id, prompt_id, type, title, narrative, facts, files, branch, source_event_ids, provider, model, created_at)
                VALUES (@session, @project, @prompt, @type, @title, @narrative, @facts, @files, @branch, @sources, @provider, @model, @now)
                """,
                new
                {
                    session = job.SessionId,
                    project = meta.ProjectId,
                    prompt = job.PromptId,
                    type = o.Type,
                    title = o.Title,
                    narrative = o.Narrative,
                    facts = ToJson(o.Facts),
                    files = ToJson(o.Files),
                    branch = meta.Branch,
                    sources = ToJson(sourceEventIds),
                    provider,
                    model,
                    now,
                },
                tx);
        }

        c.Execute("UPDATE jobs SET status = 'done', last_error = NULL, updated_at = @now WHERE id = @id", new { id = job.Id, now }, tx);
        tx.Commit();
        return observations.Count;
    }

    public void SaveSummary(JobRow job, string kind, SessionSummary summary, string provider, string model)
    {
        using var c = database.Open();
        using var tx = c.BeginTransaction();
        var meta = c.QuerySingleOrDefault<(long? ProjectId, string? Branch)>(
            "SELECT project_id AS ProjectId, branch AS Branch FROM sessions WHERE id = @id", new { id = job.SessionId }, tx);
        var now = Now();
        c.Execute(
            """
            INSERT INTO summaries (session_id, project_id, kind, request, investigated, learned, completed, next_steps, branch, provider, model, created_at)
            VALUES (@session, @project, @kind, @request, @investigated, @learned, @completed, @next, @branch, @provider, @model, @now)
            ON CONFLICT(session_id, kind) DO UPDATE SET
                request = excluded.request, investigated = excluded.investigated, learned = excluded.learned, completed = excluded.completed,
                next_steps = excluded.next_steps, branch = excluded.branch, provider = excluded.provider, model = excluded.model, created_at = excluded.created_at
            """,
            new
            {
                session = job.SessionId,
                project = meta.ProjectId,
                kind,
                request = summary.Request,
                investigated = summary.Investigated,
                learned = summary.Learned,
                completed = summary.Completed,
                next = summary.NextSteps,
                branch = meta.Branch,
                provider,
                model,
                now,
            },
            tx);
        c.Execute("UPDATE jobs SET status = 'done', last_error = NULL, updated_at = @now WHERE id = @id", new { id = job.Id, now }, tx);
        tx.Commit();
    }

    /// <summary>The note a <c>summarize-note</c> job is for, or null when it was deleted (forget) in the meantime.</summary>
    public NoteInput? LoadNote(JobRow job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!long.TryParse(job.PromptId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        using var c = database.Open();
        var row = c.QuerySingleOrDefault<(long Id, string? Name, string? Branch, string Type, string Title, string Text, string? Source, long TitleGiven, long TypeGiven)>(
            """
            SELECT o.id, p.name, o.branch, o.type, o.title, n.text, n.source, n.title_given, n.type_given
            FROM notes n JOIN observations o ON o.id = n.observation_id LEFT JOIN projects p ON p.id = o.project_id
            WHERE n.observation_id = @id
            """,
            new { id });
        return row.Id == 0 ? null : new NoteInput(row.Id, row.Name ?? "unknown", row.Branch, row.Type, row.Title, row.Text, row.Source, row.TitleGiven != 0, row.TypeGiven != 0);
    }

    /// <summary>
    /// Replaces a note's provisional narrative with the summary (keeping a title and type the caller gave), drops its
    /// vector so it is embedded again from the summary, and completes the job, in one transaction.
    /// </summary>
    public void SaveNoteSummary(JobRow job, NoteInput note, Observation summary, string provider, string model)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(summary);
        using var c = database.Open();
        using var tx = c.BeginTransaction();
        var now = Now();
        c.Execute(
            """
            UPDATE observations SET type = @type, title = @title, narrative = @narrative, facts = @facts, files = @files, model = @model
            WHERE id = @id
            """,
            new
            {
                id = note.ObservationId,
                type = note.TypeGiven ? note.Type : summary.Type,
                title = note.TitleGiven ? note.Title : summary.Title,
                narrative = summary.Narrative,
                facts = ToJson(summary.Facts),
                files = ToJson(summary.Files),
                model = $"{provider}/{model}",
            },
            tx);
        c.Execute("UPDATE notes SET summarized_at = @now WHERE observation_id = @id", new { id = note.ObservationId, now }, tx);
        c.Execute("DELETE FROM observation_vectors WHERE observation_id = @id", new { id = note.ObservationId }, tx);
        c.Execute("UPDATE jobs SET status = 'done', last_error = NULL, updated_at = @now WHERE id = @id", new { id = job.Id, now }, tx);
        tx.Commit();
    }

    private static string ToJson<T>(IReadOnlyList<T> items) => JsonSerializer.Serialize(items);

    private static string[] ParseList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
