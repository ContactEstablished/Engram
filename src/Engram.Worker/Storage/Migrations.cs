using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Engram.Worker.Storage;

internal sealed record MigrationResult(int FromVersion, int ToVersion, string? BackupPath);

/// <summary>
/// Forward-only schema migrations. Each runs in a transaction; before migrating an existing database a
/// copy goes to <c>backups/</c>. Never edit a migration once released: add a new one.
/// </summary>
internal static class Migrations
{
    // v1 (Phase 1): capture tables. v2 (Phase 2): observations, FTS, summaries, LLM call log. v3 (Phase 4): vectors. v4 (0.5.0): notes.
    private static readonly (int Version, string Sql)[] All =
    [
        (1, """
            CREATE TABLE projects (
                id          INTEGER PRIMARY KEY,
                identity    TEXT NOT NULL UNIQUE,   -- normalized git remote, else git common dir, else folder
                name        TEXT NOT NULL,
                created_at  TEXT NOT NULL
            );

            CREATE TABLE sessions (
                id              TEXT PRIMARY KEY,   -- Claude Code session_id
                project_id      INTEGER REFERENCES projects(id),
                agent           TEXT NOT NULL DEFAULT 'claude-code',
                platform        TEXT NOT NULL,      -- windows | linux | wsl (where the worker runs)
                cwd             TEXT NOT NULL,
                worktree        TEXT,               -- git toplevel of cwd
                branch          TEXT,
                model           TEXT,
                source          TEXT,               -- last SessionStart source
                transcript_path TEXT,
                started_at      TEXT NOT NULL,
                last_seen_at    TEXT NOT NULL,
                ended_at        TEXT,
                end_reason      TEXT
            );
            CREATE INDEX ix_sessions_project ON sessions(project_id, started_at);

            CREATE TABLE prompts (
                id          INTEGER PRIMARY KEY,
                session_id  TEXT NOT NULL REFERENCES sessions(id),
                prompt_id   TEXT,                   -- Claude Code prompt_id = the turn key
                text        TEXT NOT NULL,          -- redacted
                created_at  TEXT NOT NULL
            );
            CREATE UNIQUE INDEX ux_prompts_turn ON prompts(session_id, prompt_id) WHERE prompt_id IS NOT NULL;

            CREATE TABLE events (
                id            INTEGER PRIMARY KEY,
                session_id    TEXT NOT NULL REFERENCES sessions(id),
                prompt_id     TEXT,
                hook_event    TEXT NOT NULL,
                tool_name     TEXT,
                tool_use_id   TEXT,
                agent_id      TEXT,                 -- set when the call ran inside a subagent
                tool_input    TEXT,                 -- JSON, redacted and truncated
                tool_response TEXT,                 -- JSON, redacted and truncated
                text          TEXT,                 -- error message, or Stop's last assistant message
                is_error      INTEGER NOT NULL DEFAULT 0,
                duration_ms   INTEGER,
                created_at    TEXT NOT NULL,
                processed     INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX ux_events_tool_use ON events(hook_event, tool_use_id) WHERE tool_use_id IS NOT NULL;
            CREATE INDEX ix_events_turn ON events(session_id, prompt_id);
            CREATE INDEX ix_events_unprocessed ON events(created_at) WHERE processed = 0;

            CREATE TABLE jobs (
                id           INTEGER PRIMARY KEY,
                kind         TEXT NOT NULL,         -- observe-turn | summarize-checkpoint | summarize-session
                session_id   TEXT NOT NULL,
                prompt_id    TEXT,
                status       TEXT NOT NULL DEFAULT 'pending',
                attempts     INTEGER NOT NULL DEFAULT 0,
                next_run_at  TEXT NOT NULL,
                last_error   TEXT,
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            );
            CREATE UNIQUE INDEX ux_jobs_key ON jobs(kind, session_id, ifnull(prompt_id, ''));
            CREATE INDEX ix_jobs_due ON jobs(status, next_run_at);
            """),
        (2, """
            -- The memory itself. One row per thing worth remembering from a turn.
            CREATE TABLE observations (
                id               INTEGER PRIMARY KEY,
                session_id       TEXT NOT NULL REFERENCES sessions(id),
                project_id       INTEGER REFERENCES projects(id),
                prompt_id        TEXT,
                type             TEXT NOT NULL,     -- decision | bugfix | feature | change | discovery | gotcha
                title            TEXT NOT NULL,
                narrative        TEXT NOT NULL,
                facts            TEXT NOT NULL DEFAULT '[]',   -- JSON array of short strings
                files            TEXT NOT NULL DEFAULT '[]',   -- JSON array of paths
                branch           TEXT,
                source_event_ids TEXT NOT NULL DEFAULT '[]',   -- JSON array of events.id
                provider         TEXT NOT NULL,
                model            TEXT NOT NULL,
                created_at       TEXT NOT NULL
            );
            CREATE INDEX ix_observations_project ON observations(project_id, created_at);
            CREATE INDEX ix_observations_session ON observations(session_id, created_at);

            -- Keyword search (bm25), kept in sync by triggers. External content: the text lives once.
            CREATE VIRTUAL TABLE observations_fts USING fts5(
                title, narrative, facts, files,
                content='observations', content_rowid='id', tokenize='porter unicode61');
            CREATE TRIGGER observations_ai AFTER INSERT ON observations BEGIN
                INSERT INTO observations_fts(rowid, title, narrative, facts, files) VALUES (new.id, new.title, new.narrative, new.facts, new.files);
            END;
            CREATE TRIGGER observations_ad AFTER DELETE ON observations BEGIN
                INSERT INTO observations_fts(observations_fts, rowid, title, narrative, facts, files) VALUES ('delete', old.id, old.title, old.narrative, old.facts, old.files);
            END;
            CREATE TRIGGER observations_au AFTER UPDATE ON observations BEGIN
                INSERT INTO observations_fts(observations_fts, rowid, title, narrative, facts, files) VALUES ('delete', old.id, old.title, old.narrative, old.facts, old.files);
                INSERT INTO observations_fts(rowid, title, narrative, facts, files) VALUES (new.id, new.title, new.narrative, new.facts, new.files);
            END;

            -- Session summaries: 'checkpoint' (before a compaction; replaced each time) and 'final' (at session end).
            CREATE TABLE summaries (
                id            INTEGER PRIMARY KEY,
                session_id    TEXT NOT NULL REFERENCES sessions(id),
                project_id    INTEGER REFERENCES projects(id),
                kind          TEXT NOT NULL,
                request       TEXT NOT NULL,
                investigated  TEXT NOT NULL,
                learned       TEXT NOT NULL,
                completed     TEXT NOT NULL,
                next_steps    TEXT NOT NULL,
                branch        TEXT,
                provider      TEXT NOT NULL,
                model         TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                UNIQUE (session_id, kind)
            );

            -- One row per provider call: the cost log behind `engram status`.
            CREATE TABLE llm_calls (
                id                 INTEGER PRIMARY KEY,
                job_id             INTEGER,
                provider           TEXT NOT NULL,
                model              TEXT NOT NULL,
                ok                 INTEGER NOT NULL,
                error              TEXT,
                input_tokens       INTEGER,
                output_tokens      INTEGER,
                cache_read_tokens  INTEGER,
                cache_write_tokens INTEGER,
                cost_usd           REAL,          -- list-price estimate from the provider; 0 for local models
                latency_ms         INTEGER NOT NULL,
                created_at         TEXT NOT NULL
            );
            CREATE INDEX ix_llm_calls_time ON llm_calls(created_at);
            """),
        (3, """
            -- Phase 4: one embedding per observation (per model). Vectors are L2-normalized float32, little-endian.
            CREATE TABLE observation_vectors (
                observation_id INTEGER PRIMARY KEY REFERENCES observations(id) ON DELETE CASCADE,
                model          TEXT NOT NULL,
                dims           INTEGER NOT NULL,
                vector         BLOB NOT NULL,
                created_at     TEXT NOT NULL
            );
            CREATE INDEX ix_observation_vectors_model ON observation_vectors(model);
            """),
        (4, """
            -- 0.5.0: notes saved with `engram save` (e.g. a Chorus Council ruling). The observation holds the summary
            -- (what search and SessionStart use); this holds the full original text.
            CREATE TABLE notes (
                observation_id INTEGER PRIMARY KEY REFERENCES observations(id) ON DELETE CASCADE,
                client_id      TEXT UNIQUE,          -- set by the CLI; a retried or re-spooled save is stored once
                text           TEXT NOT NULL,        -- the whole note, redacted
                source         TEXT,                 -- free text from --source, e.g. 'chorus-council run 42'
                title_given    INTEGER NOT NULL,     -- 1: keep the caller's title; 0: the summary supplies it
                type_given     INTEGER NOT NULL,     -- 1: keep the caller's type; 0: the summarizer picks it
                summarized_at  TEXT,
                created_at     TEXT NOT NULL
            );
            CREATE INDEX ix_notes_source ON notes(source);
            """),
    ];

    public static int Latest => All[^1].Version;

    public static MigrationResult Apply(SqliteConnection connection, string backupDir, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.Execute("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);");
        var current = connection.ExecuteScalar<int?>("SELECT max(version) FROM schema_version") ?? 0;
        if (current > Latest)
        {
            throw new InvalidOperationException($"Database schema v{current} is newer than this worker (v{Latest}). Update Engram.");
        }

        var pending = All.Where(m => m.Version > current).ToArray();
        if (pending.Length == 0)
        {
            return new MigrationResult(current, current, null);
        }

        string? backup = null;
        if (current > 0)
        {
            Directory.CreateDirectory(backupDir);
            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            backup = Path.Combine(backupDir, $"engram-v{current}-{stamp}.db");
            connection.Execute("VACUUM INTO @backup", new { backup });
        }

        foreach (var (version, sql) in pending)
        {
            using var tx = connection.BeginTransaction();
            connection.Execute(sql, transaction: tx);
            connection.Execute(
                "INSERT INTO schema_version (version, applied_at) VALUES (@version, @at)",
                new { version, at = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) },
                tx);
            tx.Commit();
        }

        return new MigrationResult(current, Latest, backup);
    }
}
