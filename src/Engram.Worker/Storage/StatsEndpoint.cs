using System.Globalization;
using Dapper;
using Engram.Core.Security;
using Engram.Worker.Observer;

namespace Engram.Worker.Storage;

/// <summary>
/// <c>GET /stats</c> and <c>GET /recent</c> (token required): what <c>engram status</c> and
/// <c>engram recent</c> print. Reads only.
/// </summary>
internal static class StatsEndpoint
{
    private static readonly string[] Tables = ["projects", "sessions", "prompts", "events", "observations", "summaries"];

    public static void MapStatsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/stats", (HttpContext http, WorkerContext context, Database database, TimeProvider time) =>
        {
            if (!WorkerToken.Matches(http.Request.Headers.Authorization, context.Token))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            using var c = database.Open();
            var counts = Tables.ToDictionary(t => t, t => c.ExecuteScalar<long>($"SELECT count(*) FROM {t}"));
            var jobs = c.Query<(string Status, long N)>("SELECT status, count(*) FROM jobs GROUP BY status").ToDictionary(r => r.Status, r => r.N);
            var since = ObserverStore.Iso(time.GetUtcNow().AddHours(-24));
            var calls = c.QuerySingle<(long Calls, long Failed, long? In, long? Out, double? Cost)>(
                """
                SELECT count(*), sum(1 - ok), sum(input_tokens + coalesce(cache_read_tokens, 0) + coalesce(cache_write_tokens, 0)), sum(output_tokens), sum(cost_usd)
                FROM llm_calls WHERE created_at >= @since
                """,
                new { since });
            var lastError = c.QueryFirstOrDefault<string>("SELECT last_error FROM jobs WHERE status IN ('parked', 'pending') AND last_error IS NOT NULL ORDER BY updated_at DESC LIMIT 1");
            return Results.Json(new
            {
                counts,
                jobs = new
                {
                    pending = jobs.GetValueOrDefault("pending"),
                    running = jobs.GetValueOrDefault("running"),
                    done = jobs.GetValueOrDefault("done"),
                    parked = jobs.GetValueOrDefault("parked"),
                    last_error = lastError,
                },
                observer = new
                {
                    provider = context.Settings.Observer.Provider,
                    model = context.Settings.Observer.Model,
                    max_calls_per_hour = context.Settings.Observer.MaxCallsPerHour,
                    calls_24h = calls.Calls,
                    failed_24h = calls.Failed,
                    input_tokens_24h = calls.In ?? 0,
                    output_tokens_24h = calls.Out ?? 0,
                    cost_usd_24h = Math.Round(calls.Cost ?? 0, 4),
                },
                db_bytes = new FileInfo(database.Path).Length,
            });
        });

        app.MapGet("/recent", (HttpContext http, WorkerContext context, Database database) =>
        {
            if (!WorkerToken.Matches(http.Request.Headers.Authorization, context.Token))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var limit = int.TryParse(http.Request.Query["limit"], NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 1, 100) : 10;
            using var c = database.Open();
            var observations = c.Query(
                """
                SELECT o.id, o.type, o.title, o.narrative, o.facts, o.files, o.branch, o.created_at, p.name AS project
                FROM observations o LEFT JOIN projects p ON p.id = o.project_id
                ORDER BY o.id DESC LIMIT @limit
                """,
                new { limit });
            var summaries = c.Query(
                """
                SELECT s.session_id, s.kind, s.request, s.investigated, s.learned, s.completed, s.next_steps, s.branch, s.created_at, p.name AS project
                FROM summaries s LEFT JOIN projects p ON p.id = s.project_id
                ORDER BY s.created_at DESC LIMIT @limit
                """,
                new { limit });
            return Results.Json(new { observations, summaries });
        });
    }
}
