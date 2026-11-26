using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Engram.Worker.Observer;
using Engram.Worker.Storage;

namespace Engram.Worker.Memory;

internal sealed record ObservationRow(long Id, string Type, string Title, string Narrative, string Facts, string Files, string? Branch, string CreatedAt, string SessionId, string? PromptId, string? Project, string Provider);

internal sealed record SummaryRow(string SessionId, string Kind, string Request, string Investigated, string Learned, string Completed, string NextSteps, string? Branch, string CreatedAt);

/// <summary>A note as <c>engram save</c> sent it (text already redacted).</summary>
internal sealed record NoteText(string Text, string? Source, bool TitleGiven, bool TypeGiven, string? ClientId);

internal sealed record NoteRow(long Id, string Text, string? Source, string? SummarizedAt);

internal sealed record SearchQuery(string Text, long? ProjectId, string? Type, string? Since, string? Branch, int Limit);

/// <summary>Reads (and the two writes: save, forget) behind injection and the MCP tools.</summary>
internal sealed partial class MemoryStore(Database database, TimeProvider time)
{
    private const string RowColumns = """
        o.id AS Id, o.type AS Type, o.title AS Title, o.narrative AS Narrative, o.facts AS Facts, o.files AS Files,
        o.branch AS Branch, o.created_at AS CreatedAt, o.session_id AS SessionId, o.prompt_id AS PromptId, p.name AS Project, o.provider AS Provider
        """;

    public long? FindProject(string identity)
    {
        using var c = database.Open();
        return c.ExecuteScalar<long?>("SELECT id FROM projects WHERE identity = @identity", new { identity });
    }

    public long CountObservations(long projectId)
    {
        using var c = database.Open();
        return c.ExecuteScalar<long>("SELECT count(*) FROM observations WHERE project_id = @projectId", new { projectId });
    }

    /// <summary>The newest observations of a project: the candidate pool for injection ranking.</summary>
    public IReadOnlyList<ObservationRow> RecentForProject(long projectId, int limit)
    {
        using var c = database.Open();
        return [.. c.Query<ObservationRow>(
            $"SELECT {RowColumns} FROM observations o LEFT JOIN projects p ON p.id = o.project_id WHERE o.project_id = @projectId ORDER BY o.id DESC LIMIT @limit",
            new { projectId, limit })];
    }

    /// <summary>The latest summary of another session in this project, preferring the same branch.</summary>
    public SummaryRow? LastSummary(long projectId, string? branch, string excludeSessionId)
    {
        using var c = database.Open();
        return c.QueryFirstOrDefault<SummaryRow>(
            """
            SELECT session_id AS SessionId, kind AS Kind, request AS Request, investigated AS Investigated, learned AS Learned,
                   completed AS Completed, next_steps AS NextSteps, branch AS Branch, created_at AS CreatedAt
            FROM summaries
            WHERE project_id = @projectId AND session_id <> @excludeSessionId
            ORDER BY (branch IS @branch) DESC, (kind = 'final') DESC, created_at DESC
            LIMIT 1
            """,
            new { projectId, branch, excludeSessionId });
    }

    /// <summary>
    /// Keyword search, ranked by bm25 with the title weighted highest. All words must match; if nothing does,
    /// any word may (so a long natural-language question still finds something).
    /// </summary>
    public IReadOnlyList<ObservationRow> Search(SearchQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var terms = WordRegex().Matches(q.Text).Select(m => m.Value).Where(t => t.Length > 1 && !StopWords.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        using var c = database.Open();
        if (terms.Count == 0)
        {
            // No query: newest first. A query of only stop words matches nothing by keyword.
            return string.IsNullOrWhiteSpace(q.Text) ? Filtered(c, q, match: null) : [];
        }

        var all = string.Join(" ", terms.Select(Term));
        var results = Filtered(c, q, all);
        return results.Count > 0 || terms.Count == 1 ? results : Filtered(c, q, string.Join(" OR ", terms.Select(Term)));
    }

    // Question words carry no meaning for keyword search and, prefix-matched, hit everything ("us" -> "uses").
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "did", "do", "does", "for", "from", "how", "i", "in", "is", "it",
        "of", "on", "or", "our", "should", "that", "the", "this", "to", "us", "was", "we", "were", "what", "when", "where",
        "which", "who", "why", "with", "you", "your",
    };

    /// <summary>A quoted FTS5 term; prefix-matched only when long enough for the prefix to mean something.</summary>
    private static string Term(string word) => word.Length >= 4 ? $"\"{word}\"*" : $"\"{word}\"";

    private static List<ObservationRow> Filtered(Microsoft.Data.Sqlite.SqliteConnection c, SearchQuery q, string? match)
    {
        var where = new StringBuilder("WHERE 1 = 1");
        if (match is not null)
        {
            where.Append(" AND observations_fts MATCH @match");
        }

        if (q.ProjectId is not null)
        {
            where.Append(" AND o.project_id = @ProjectId");
        }

        if (q.Type is not null)
        {
            where.Append(" AND o.type = @Type");
        }

        if (q.Since is not null)
        {
            where.Append(" AND o.created_at >= @Since");
        }

        if (q.Branch is not null)
        {
            where.Append(" AND o.branch = @Branch");
        }

        var sql = match is null
            ? $"SELECT {RowColumns} FROM observations o LEFT JOIN projects p ON p.id = o.project_id {where} ORDER BY o.id DESC LIMIT @Limit"
            : $"""
               SELECT {RowColumns} FROM observations_fts f
               JOIN observations o ON o.id = f.rowid
               LEFT JOIN projects p ON p.id = o.project_id
               {where}
               ORDER BY bm25(observations_fts, 5.0, 2.0, 1.0, 1.0)
               LIMIT @Limit
               """;
        return [.. c.Query<ObservationRow>(sql, new { match, q.ProjectId, q.Type, q.Since, q.Branch, q.Limit })];
    }

    /// <summary>Observations of the same project around <paramref name="id"/>, oldest first.</summary>
    public IReadOnlyList<ObservationRow> Timeline(long id, int before, int after)
    {
        using var c = database.Open();
        var anchor = c.QuerySingleOrDefault<(long Id, long? ProjectId)>("SELECT id, project_id FROM observations WHERE id = @id", new { id });
        if (anchor.Id == 0)
        {
            return [];
        }

        var earlier = c.Query<ObservationRow>(
            $"SELECT {RowColumns} FROM observations o LEFT JOIN projects p ON p.id = o.project_id WHERE o.project_id IS @pid AND o.id < @id ORDER BY o.id DESC LIMIT @before",
            new { pid = anchor.ProjectId, id, before }).Reverse();
        var rest = c.Query<ObservationRow>(
            $"SELECT {RowColumns} FROM observations o LEFT JOIN projects p ON p.id = o.project_id WHERE o.project_id IS @pid AND o.id >= @id ORDER BY o.id LIMIT @take",
            new { pid = anchor.ProjectId, id, take = after + 1 });
        return [.. earlier, .. rest];
    }

    public IReadOnlyList<ObservationRow> Get(IReadOnlyList<long> ids)
    {
        using var c = database.Open();
        return [.. c.Query<ObservationRow>($"SELECT {RowColumns} FROM observations o LEFT JOIN projects p ON p.id = o.project_id WHERE o.id IN @ids ORDER BY o.id", new { ids })];
    }

    /// <summary>
    /// Stores a memory Claude was explicitly asked to keep. It hangs off a per-project "manual" session so the
    /// schema's session link still holds.
    /// </summary>
    public long Save(long projectId, string platform, string cwd, string? branch, Observation memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var now = ObserverStore.Iso(time.GetUtcNow());
        var sessionId = string.Create(CultureInfo.InvariantCulture, $"engram-manual:{projectId}");
        using var c = database.Open();
        using var tx = c.BeginTransaction();
        c.Execute(
            """
            INSERT INTO sessions (id, project_id, agent, platform, cwd, branch, started_at, last_seen_at)
            VALUES (@sessionId, @projectId, 'manual', @platform, @cwd, @branch, @now, @now)
            ON CONFLICT(id) DO UPDATE SET last_seen_at = excluded.last_seen_at
            """,
            new { sessionId, projectId, platform, cwd, branch, now },
            tx);
        var id = c.ExecuteScalar<long>(
            """
            INSERT INTO observations (session_id, project_id, type, title, narrative, facts, files, branch, provider, model, created_at)
            VALUES (@sessionId, @projectId, @type, @title, @narrative, @facts, @files, @branch, 'manual', 'save_memory', @now)
            RETURNING id
            """,
            new
            {
                sessionId,
                projectId,
                type = memory.Type,
                title = memory.Title,
                narrative = memory.Narrative,
                facts = JsonSerializer.Serialize(memory.Facts),
                files = JsonSerializer.Serialize(memory.Files),
                branch,
                now,
            },
            tx);
        tx.Commit();
        return id;
    }

    /// <summary>
    /// Stores a note from <c>engram save</c>: an observation whose narrative is a provisional clip of the text until
    /// the summarizer replaces it, the full text in <c>notes</c>, and a <c>summarize-note</c> job, in one transaction.
    /// A <see cref="NoteText.ClientId"/> seen before returns the existing note instead of storing a second copy.
    /// </summary>
    public (long Id, bool Existing) SaveNote(long projectId, string platform, string cwd, string? branch, Observation provisional, NoteText note, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(provisional);
        ArgumentNullException.ThrowIfNull(note);
        using var c = database.Open();
        using var tx = c.BeginTransaction();
        if (note.ClientId is not null && c.ExecuteScalar<long?>("SELECT observation_id FROM notes WHERE client_id = @id", new { id = note.ClientId }, tx) is { } existing)
        {
            return (existing, true);
        }

        var now = ObserverStore.Iso(time.GetUtcNow());
        var created = ObserverStore.Iso(createdAt);
        var sessionId = string.Create(CultureInfo.InvariantCulture, $"engram-manual:{projectId}");
        c.Execute(
            """
            INSERT INTO sessions (id, project_id, agent, platform, cwd, branch, started_at, last_seen_at)
            VALUES (@sessionId, @projectId, 'manual', @platform, @cwd, @branch, @now, @now)
            ON CONFLICT(id) DO UPDATE SET last_seen_at = excluded.last_seen_at
            """,
            new { sessionId, projectId, platform, cwd, branch, now },
            tx);
        var id = c.ExecuteScalar<long>(
            """
            INSERT INTO observations (session_id, project_id, type, title, narrative, facts, files, branch, provider, model, created_at)
            VALUES (@sessionId, @projectId, @type, @title, @narrative, '[]', '[]', @branch, 'note', 'pending', @created)
            RETURNING id
            """,
            new { sessionId, projectId, type = provisional.Type, title = provisional.Title, narrative = provisional.Narrative, branch, created },
            tx);
        c.Execute(
            """
            INSERT INTO notes (observation_id, client_id, text, source, title_given, type_given, created_at)
            VALUES (@id, @clientId, @text, @source, @titleGiven, @typeGiven, @created)
            """,
            new { id, clientId = note.ClientId, text = note.Text, source = note.Source, titleGiven = note.TitleGiven, typeGiven = note.TypeGiven, created },
            tx);
        c.Execute(
            """
            INSERT INTO jobs (kind, session_id, prompt_id, next_run_at, created_at, updated_at)
            VALUES (@kind, @sessionId, @promptId, @now, @now, @now)
            """,
            new { kind = JobKinds.SummarizeNote, sessionId, promptId = id.ToString(CultureInfo.InvariantCulture), now },
            tx);
        tx.Commit();
        return (id, false);
    }

    /// <summary>The note behind each of <paramref name="ids"/> that is one (saved with <c>engram save</c>).</summary>
    public IReadOnlyDictionary<long, NoteRow> Notes(IReadOnlyList<long> ids)
    {
        using var c = database.Open();
        return c.Query<NoteRow>(
                "SELECT observation_id AS Id, text AS Text, source AS Source, summarized_at AS SummarizedAt FROM notes WHERE observation_id IN @ids",
                new { ids })
            .ToDictionary(n => n.Id);
    }

    /// <summary>Deletes one observation (the FTS trigger removes its index entry). Returns false if it didn't exist.</summary>
    public bool Forget(long id)
    {
        using var c = database.Open();
        return c.Execute("DELETE FROM observations WHERE id = @id", new { id }) > 0;
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
