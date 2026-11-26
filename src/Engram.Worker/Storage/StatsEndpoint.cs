using System.Globalization;
using System.Text;
using Dapper;
using Engram.Core.Security;

namespace Engram.Worker.Storage;

/// <summary><c>GET /stats</c> (token required): row counts and database size, shown by <c>engram status</c>.</summary>
internal static class StatsEndpoint
{
    private static readonly string[] Tables = ["projects", "sessions", "prompts", "events", "jobs"];

    public static void MapStatsEndpoint(this IEndpointRouteBuilder app) => app.MapGet("/stats", (HttpContext http, WorkerContext context, Database database) =>
    {
        if (!WorkerToken.Matches(http.Request.Headers.Authorization, context.Token))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        using var c = database.Open();
        var json = new StringBuilder("{");
        foreach (var table in Tables)
        {
            json.Append(CultureInfo.InvariantCulture, $"\"{table}\":{c.ExecuteScalar<long>($"SELECT count(*) FROM {table}")},");
        }

        var pending = c.ExecuteScalar<long>("SELECT count(*) FROM jobs WHERE status = 'pending'");
        var size = new FileInfo(database.Path).Length;
        json.Append(CultureInfo.InvariantCulture, $"\"pending_jobs\":{pending},\"db_bytes\":{size}}}");
        return Results.Text(json.ToString(), "application/json");
    });
}
