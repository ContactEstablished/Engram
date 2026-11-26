using System.Globalization;
using System.Text;
using System.Text.Json;
using Engram.Core.Hooks;
using Engram.Core.Security;
using Engram.Worker.Capture;
using Engram.Worker.Embedding;
using Engram.Worker.Observer;
using Engram.Worker.Storage;

namespace Engram.Worker.Memory;

/// <summary>
/// The read side, as text: <c>POST /context</c> for SessionStart and <c>/memory/*</c> behind the MCP tools and
/// <c>engram search</c>. Answers are compact plain text so the CLI can relay them to Claude unchanged.
/// </summary>
internal static class MemoryEndpoints
{
    public const int DefaultSearchLimit = 10;

    public static void MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/context", ContextAsync);
        app.MapGet("/memory/search", SearchAsync);
        app.MapGet("/memory/timeline", Timeline);
        app.MapGet("/memory/get", Get);
        app.MapPost("/memory/save", SaveAsync);
        app.MapPost("/memory/forget", Forget);
    }

    private static bool Authorized(HttpContext http, WorkerContext context) => WorkerToken.Matches(http.Request.Headers.Authorization, context.Token);

    private static IResult Text(string text, int status = 200) => Results.Text(text, "text/plain; charset=utf-8", Encoding.UTF8, status);

    /// <summary>
    /// SessionStart: registers the session (through the normal capture queue) and returns the context to inject,
    /// or an empty body when there is nothing to say.
    /// </summary>
    private static async Task<IResult> ContextAsync(HttpContext http, WorkerContext context, CaptureQueue queue, ContextBuilder builder, EmbeddingService embeddings)
    {
        if (!Authorized(http, context))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        // A session is starting: make sure the embedding model is loaded before Claude searches.
        embeddings.RequestWarmUp();

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted).ConfigureAwait(false);
        var body = buffer.ToArray();
        if (body.Length == 0 || body.Length > HookEndpoints.MaxBodyBytes)
        {
            return Text(string.Empty);
        }

        queue.TryEnqueue(new IngestItem(body, "context"));
        try
        {
            if (HookPayloadReader.Read(body) is not SessionStartPayload start)
            {
                return Text(string.Empty);
            }

            return Text(builder.Build(start.Cwd, start.SessionId, start.Source));
        }
        catch (JsonException)
        {
            return Text(string.Empty);
        }
    }

    private static async Task<IResult> SearchAsync(HttpContext http, WorkerContext context, MemoryStore store, ProjectResolver projects, HybridSearcher searcher)
    {
        if (!Authorized(http, context))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var q = http.Request.Query;
        var text = q["q"].ToString();
        var all = q["all"] == "1" || q["all"] == "true";
        var type = q["type"].ToString() is { Length: > 0 } t ? ObservationTypes.Normalize(t) : null;
        if (q["type"].ToString().Length > 0 && type is null)
        {
            return Text($"Unknown type '{q["type"]}'. Types: {string.Join(", ", ObservationTypes.All)}.", 400);
        }

        if (!TryParseSince(q["since"].ToString(), out var since))
        {
            return Text($"Can't read since='{q["since"]}'. Use a date (2026-11-01) or a number of days (14d).", 400);
        }

        long? projectId = null;
        string scope = "all repos";
        if (!all)
        {
            var cwd = q["cwd"].ToString();
            if (string.IsNullOrWhiteSpace(cwd))
            {
                return Text("search needs the repo folder (cwd), or all=1.", 400);
            }

            var project = projects.Resolve(cwd);
            projectId = store.FindProject(project.Identity);
            scope = $"this repository (project \"{project.Name}\")";
            if (projectId is null)
            {
                return Text($"No memory recorded for {scope} yet.");
            }
        }

        var limit = int.TryParse(q["limit"], NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 1, 50) : DefaultSearchLimit;
        var branch = q["branch"].ToString() is { Length: > 0 } b ? b : null;
        var mode = q["mode"].ToString().ToLowerInvariant() switch
        {
            "keyword" => SearchMode.Keyword,
            "semantic" => SearchMode.Semantic,
            _ => SearchMode.Hybrid,
        };
        var outcome = await searcher.SearchAsync(new SearchQuery(text, projectId, type, since, branch, limit), mode, http.RequestAborted).ConfigureAwait(false);
        var rows = outcome.Rows;
        var note = outcome.Note is null ? string.Empty : $" ({outcome.Note})";
        if (rows.Count == 0)
        {
            return Text((text.Trim().Length == 0 ? $"No observations in {scope}." : $"No observations in {scope} match \"{text.Trim()}\".") + note);
        }

        var how = outcome.Used switch
        {
            SearchMode.Hybrid => "by keyword and meaning",
            SearchMode.Semantic => "by meaning",
            _ => "by keyword",
        };
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{rows.Count} result(s) in {scope}{(text.Trim().Length > 0 ? $" for \"{text.Trim()}\"" : string.Empty)}, best first {how}{note}. get_observations returns full records.\n");
        foreach (var row in rows)
        {
            sb.Append(IndexRow(row, includeProject: all)).Append('\n');
        }

        return Text(sb.ToString());
    }

    private static IResult Timeline(HttpContext http, WorkerContext context, MemoryStore store)
    {
        if (!Authorized(http, context))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var q = http.Request.Query;
        if (!long.TryParse(q["id"], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return Text("timeline needs an observation id.", 400);
        }

        int Count(string name) => int.TryParse(q[name], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 25) : 5;
        var rows = store.Timeline(id, Count("before"), Count("after"));
        if (rows.Count == 0)
        {
            return Text($"No observation #{id}.", 404);
        }

        var sb = new StringBuilder($"Observations around #{id} in time order:\n");
        foreach (var row in rows)
        {
            sb.Append(row.Id == id ? "> " : "  ").Append(IndexRow(row, includeProject: false)).Append('\n');
        }

        return Text(sb.ToString());
    }

    private static IResult Get(HttpContext http, WorkerContext context, MemoryStore store)
    {
        if (!Authorized(http, context))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var ids = http.Request.Query["ids"].ToString()
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n > 0)
            .Distinct()
            .Take(20)
            .ToList();
        if (ids.Count == 0)
        {
            return Text("get_observations needs one or more ids.", 400);
        }

        var rows = store.Get(ids);
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(FullRecord(row)).Append('\n');
        }

        var missing = ids.Except(rows.Select(r => r.Id)).ToList();
        if (missing.Count > 0)
        {
            sb.Append("Not found: ").Append(string.Join(", ", missing.Select(m => "#" + m.ToString(CultureInfo.InvariantCulture)))).Append('\n');
        }

        return Text(sb.ToString());
    }

    private sealed record SaveRequest(string? Text, string? Title, string? Type, string[]? Files, string? Cwd);

    private static async Task<IResult> SaveAsync(HttpContext http, WorkerContext context, MemoryStore store, ProjectResolver projects, CaptureStore capture, TimeProvider time, EmbeddingSignal embeddingSignal)
    {
        if (!Authorized(http, context))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        SaveRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<SaveRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web), http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Text("save_memory needs JSON: {\"text\": ..., \"cwd\": ...}.", 400);
        }

        if (request?.Text is not { Length: > 0 } || string.IsNullOrWhiteSpace(request.Cwd))
        {
            return Text("save_memory needs text and the repo folder (cwd).", 400);
        }

        var type = request.Type is { Length: > 0 } ? ObservationTypes.Normalize(request.Type) : "discovery";
        if (type is null)
        {
            return Text($"Unknown type '{request.Type}'. Types: {string.Join(", ", ObservationTypes.All)}.", 400);
        }

        var redactor = new Redactor(context.Settings.Capture.RedactPatterns);
        var narrative = Clip(redactor.Redact(request.Text.Trim()), 1500);
        var title = Clip(redactor.Redact(request.Title is { Length: > 0 } ? request.Title.Trim() : DeriveTitle(request.Text)), 120);
        var project = projects.Resolve(request.Cwd);
        var projectId = capture.GetOrCreateProject(project.Identity, project.Name, time.GetUtcNow());
        var id = store.Save(projectId, context.Platform, request.Cwd, project.Branch, new Observation(type, title, narrative, [], [.. (request.Files ?? []).Take(12)]));
        embeddingSignal.Notify();
        return Text(string.Create(CultureInfo.InvariantCulture, $"Saved as #{id} ({type}) for this repository (project \"{project.Name}\")."));
    }

    private static IResult Forget(HttpContext http, WorkerContext context, MemoryStore store, VectorIndex vectors)
    {
        if (!Authorized(http, context))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (!long.TryParse(http.Request.Query["id"].ToString().TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return Text("forget needs an observation id.", 400);
        }

        vectors.Remove(id); // the row's vector goes with it (ON DELETE CASCADE)
        return store.Forget(id) ? Text($"Deleted #{id}.") : Text($"No observation #{id}.", 404);
    }

    internal static string IndexRow(ObservationRow o, bool includeProject)
    {
        var files = Files(o.Files);
        var where = includeProject ? $"{o.Project}{(o.Branch is null ? string.Empty : "/" + o.Branch)}  " : string.Empty;
        var fileText = files.Count == 0 ? string.Empty : $" ({string.Join(", ", files.Take(3).Select(Path.GetFileName))})";
        return $"#{o.Id} {o.Type,-9} {Date(o.CreatedAt, "yyyy-MM-dd")}  {where}{OneLine(o.Title)}{fileText}";
    }

    internal static string FullRecord(ObservationRow o)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"#{o.Id} {o.Type} | {Date(o.CreatedAt, "yyyy-MM-dd HH:mm")} | repo {o.Project ?? "?"}{(o.Branch is null ? string.Empty : " | branch " + o.Branch)}{(o.Provider == "manual" ? " | saved on request" : string.Empty)}\n");
        sb.Append(OneLine(o.Title)).Append('\n');
        sb.Append(o.Narrative.Trim()).Append('\n');
        var facts = Files(o.Facts);
        foreach (var fact in facts)
        {
            sb.Append("- ").Append(OneLine(fact)).Append('\n');
        }

        var files = Files(o.Files);
        if (files.Count > 0)
        {
            sb.Append("Files: ").Append(string.Join(", ", files)).Append('\n');
        }

        return sb.ToString();
    }

    internal static bool TryParseSince(string value, out string? since)
    {
        since = null;
        value = value.Trim();
        if (value.Length == 0)
        {
            return true;
        }

        if (value.EndsWith('d') && int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var days))
        {
            since = ObserverStore.Iso(DateTimeOffset.UtcNow.AddDays(-days));
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
        {
            since = ObserverStore.Iso(date);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The first sentence, whole when it fits in 120 characters. A visibly clipped title makes Claude doubt the
    /// record ("the title is truncated"), so clipping is the last resort, at a word boundary.
    /// </summary>
    internal static string DeriveTitle(string text)
    {
        var line = OneLine(text);
        var end = line.Length;
        foreach (var stop in new[] { ". ", "! ", "? ", "; " })
        {
            var i = line.IndexOf(stop, StringComparison.Ordinal);
            if (i > 0 && i < end)
            {
                end = i;
            }
        }

        var first = line[..end].TrimEnd('.', '!', '?', ';');
        if (first.Length <= 120)
        {
            return first;
        }

        var cut = first.LastIndexOf(' ', 118);
        return first[..(cut > 40 ? cut : 118)] + "…";
    }

    private static List<string> Files(string json)
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

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
