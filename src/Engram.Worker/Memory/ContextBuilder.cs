using System.Globalization;
using System.Text;
using System.Text.Json;
using Engram.Core.Settings;
using Engram.Worker.Capture;

namespace Engram.Worker.Memory;

/// <summary>
/// Builds the text SessionStart injects: a header, the last session summary, then the best observations as
/// one-line index rows. Ranked by type weight × recency × same-branch boost, shown newest first, cut to the
/// character budget. Written as plain facts: text that reads like system commands can trip Claude Code's
/// prompt-injection defenses.
/// </summary>
internal sealed class ContextBuilder(MemoryStore store, ProjectResolver projects, TimeProvider time, WorkerContext context)
{
    private const int CandidatePool = 500;

    /// <summary>Sources where context is injected. A resumed (or forked) session replays its earlier injection.</summary>
    public static bool ShouldInject(string? source) => source is not ("resume" or "fork");

    public string Build(string cwd, string sessionId, string? source)
    {
        var settings = context.Settings.Injection;
        if (!settings.Enabled || settings.MaxRows == 0 || !ShouldInject(source))
        {
            return string.Empty;
        }

        var project = projects.Resolve(cwd);
        if (store.FindProject(project.Identity) is not { } projectId)
        {
            return string.Empty;
        }

        var candidates = store.RecentForProject(projectId, CandidatePool);
        var summary = store.LastSummary(projectId, project.Branch, sessionId);
        if (candidates.Count == 0 && summary is null)
        {
            return string.Empty;
        }

        var now = time.GetUtcNow();
        var ranked = candidates
            .Select(o => (Row: o, Score: Score(o, project.Branch, now, settings)))
            .OrderByDescending(x => x.Score)
            .Take(settings.MaxRows)
            .Select(x => x.Row)
            .OrderByDescending(o => o.Id)
            .ToList();

        var total = store.CountObservations(projectId);
        var lines = new List<string>();
        var branchText = project.Branch is null ? string.Empty : $" (branch {project.Branch})";
        var shownPlaceholder = "{shown}";
        lines.Add($"Engram memory for repo \"{project.Name}\"{branchText}: {shownPlaceholder} of {total} observations, most relevant first by type and recency.");
        if (summary is not null)
        {
            lines.Add(SummaryLine(summary));
        }

        var header = string.Join('\n', lines);
        const string footer = "Full records and older history are available through the engram MCP tools (search, timeline, get_observations).";
        var budget = settings.MaxChars - header.Length - footer.Length - 20;
        var rows = new List<string>();
        foreach (var o in ranked)
        {
            var row = Row(o);
            if (budget - row.Length - 1 < 0)
            {
                break;
            }

            budget -= row.Length + 1;
            rows.Add(row);
        }

        header = header.Replace(shownPlaceholder, rows.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var sb = new StringBuilder(header).Append('\n');
        foreach (var row in rows)
        {
            sb.Append(row).Append('\n');
        }

        return sb.Append(footer).Append('\n').ToString();
    }

    internal static double Score(ObservationRow o, string? branch, DateTimeOffset now, InjectionSettings settings)
    {
        var weight = settings.TypeWeights.TryGetValue(o.Type, out var w) ? w
            : settings.TypeWeights.FirstOrDefault(kv => string.Equals(kv.Key, o.Type, StringComparison.OrdinalIgnoreCase)).Value is var v and > 0 ? v : 1;
        var created = DateTimeOffset.TryParse(o.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : now;
        var ageDays = Math.Max(0, (now - created).TotalDays);
        var recency = Math.Pow(0.5, ageDays / settings.HalfLifeDays);
        var sameBranch = branch is not null && string.Equals(o.Branch, branch, StringComparison.Ordinal) ? settings.SameBranchBoost : 1;
        return weight * recency * sameBranch;
    }

    private static string Row(ObservationRow o)
    {
        var files = ParseFiles(o.Files);
        var fileText = files.Count == 0 ? string.Empty : $" ({string.Join(", ", files.Take(2).Select(Path.GetFileName))}{(files.Count > 2 ? ", …" : string.Empty)})";
        return $"#{o.Id} {o.Type,-9} {Date(o.CreatedAt, "MM-dd")}  {OneLine(o.Title)}{fileText}";
    }

    private static string SummaryLine(SummaryRow s)
    {
        var parts = new List<string>();
        if (s.Request.Length > 0)
        {
            parts.Add(OneLine(s.Request));
        }

        if (s.Completed.Length > 0)
        {
            parts.Add("done: " + OneLine(s.Completed));
        }

        if (s.NextSteps.Length > 0)
        {
            parts.Add("open: " + OneLine(s.NextSteps));
        }

        var branch = s.Branch is null ? string.Empty : $" on {s.Branch}";
        return Trim($"Last session {Date(s.CreatedAt, "yyyy-MM-dd")}{branch}: {string.Join("; ", parts)}", 700);
    }

    private static List<string> ParseFiles(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Date(string iso, string format) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)
            : "?";

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
