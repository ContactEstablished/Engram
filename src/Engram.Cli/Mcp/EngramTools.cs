using System.Globalization;
using System.Text;
using System.Text.Json;
using Engram.Cli.Worker;
using Engram.Core;
using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Cli.Mcp;

/// <summary>How the tools reach the worker. Swappable for tests.</summary>
internal interface IMemoryApi
{
    (int? Status, string? Body) Get(string pathAndQuery);

    (int? Status, string? Body) Post(string pathAndQuery, byte[] body);
}

/// <summary>Talks to the local worker, starting it on first use if it isn't running.</summary>
internal sealed class WorkerMemoryApi(Func<string, string?> getEnv) : IMemoryApi
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly string _home = EngramPaths.Home(getEnv);
    private readonly int _port = SettingsLoader.Load(EngramPaths.Home(getEnv), getEnv).Settings.Worker.Port;

    public (int? Status, string? Body) Get(string pathAndQuery) => WithWorker(token => WorkerClient.Get(_port, pathAndQuery, Timeout, token));

    public (int? Status, string? Body) Post(string pathAndQuery, byte[] body) => WithWorker(token => WorkerClient.PostForBody(_port, pathAndQuery, token, body, Timeout));

    private (int? Status, string? Body) WithWorker(Func<string, (int? Status, string? Body)> call)
    {
        if (WorkerToken.Read(_home) is not { } token)
        {
            return (null, "Engram isn't installed (no token). Run `engram install`.");
        }

        var result = call(token);
        if (result.Status is not null)
        {
            return result;
        }

        return WorkerLauncher.EnsureRunning(_port, _home, getEnv, TimeSpan.FromSeconds(5)) is { } error
            ? (null, $"The Engram worker isn't running and couldn't be started: {error}")
            : call(token);
    }
}

/// <summary>The five tools from the plan. Results are the worker's text, relayed unchanged.</summary>
internal static class EngramTools
{
    public const string Instructions =
        "Engram is long-term memory for the repositories you work in: short observations (decisions, bug fixes, gotchas, " +
        "features, changes, discoveries) recorded from earlier sessions. A short index is added at session start. " +
        "Use search to find more, get_observations for full records, timeline for what happened around one, " +
        "save_memory when the user asks you to remember something, and forget when they ask you to delete one.";

    private const string TypeEnum = """["decision","bugfix","feature","change","discovery","gotcha"]""";

    public static IReadOnlyList<McpTool> Create(IMemoryApi api, Func<string> cwd) =>
    [
        new(
            "search",
            "Search Engram's memory of this repository by keyword and by meaning (best matches first), so a plain question works. Returns one line per observation with its id. " +
            "Use it before re-investigating something that may have been solved or decided in an earlier session.",
            $$"""
            {"type":"object","properties":{
              "query":{"type":"string","description":"Words to look for, e.g. 'webhook retry' or a question."},
              "type":{"type":"string","enum":{{TypeEnum}},"description":"Only this kind of observation."},
              "since":{"type":"string","description":"Only newer than this: a date (2026-11-01) or days ('14d')."},
              "branch":{"type":"string","description":"Only observations made on this git branch."},
              "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Default 10."},
              "all_repos":{"type":"boolean","description":"Search every repository, not just this one."},
              "mode":{"type":"string","enum":["hybrid","keyword","semantic"],"description":"Default hybrid (keyword and meaning)."}
            },"required":["query"],"additionalProperties":false}
            """,
            args =>
            {
                var query = new StringBuilder("/memory/search?q=").Append(Uri.EscapeDataString(Required(args, "query")));
                Append(query, "type", Str(args, "type"));
                Append(query, "since", Str(args, "since"));
                Append(query, "branch", Str(args, "branch"));
                Append(query, "mode", Str(args, "mode"));
                Append(query, "limit", Int(args, "limit")?.ToString(CultureInfo.InvariantCulture));
                if (Bool(args, "all_repos") == true)
                {
                    query.Append("&all=1");
                }
                else
                {
                    Append(query, "cwd", cwd());
                }

                return Relay(api.Get(query.ToString()));
            }),
        new(
            "timeline",
            "Show the observations recorded just before and after one observation in this repository, in time order. Useful to see what led to a decision or fix.",
            """
            {"type":"object","properties":{
              "id":{"type":"integer","description":"Observation id, e.g. 4121 from '#4121'."},
              "before":{"type":"integer","minimum":0,"maximum":25,"description":"How many earlier observations (default 5)."},
              "after":{"type":"integer","minimum":0,"maximum":25,"description":"How many later observations (default 5)."}
            },"required":["id"],"additionalProperties":false}
            """,
            args =>
            {
                var query = new StringBuilder("/memory/timeline?id=").Append(RequiredInt(args, "id").ToString(CultureInfo.InvariantCulture));
                Append(query, "before", Int(args, "before")?.ToString(CultureInfo.InvariantCulture));
                Append(query, "after", Int(args, "after")?.ToString(CultureInfo.InvariantCulture));
                return Relay(api.Get(query.ToString()));
            }),
        new(
            "get_observations",
            "Get full records (narrative, facts, files) for observations by id. Ids come from the session-start index, search or timeline.",
            """
            {"type":"object","properties":{
              "ids":{"type":"array","items":{"type":"integer"},"minItems":1,"maxItems":20,"description":"Observation ids."}
            },"required":["ids"],"additionalProperties":false}
            """,
            args =>
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
                {
                    throw new ArgumentException("ids is required: a list of observation ids.");
                }

                var list = string.Join(',', ids.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.Number ? i.GetInt64().ToString(CultureInfo.InvariantCulture) : i.ToString()));
                return Relay(api.Get("/memory/get?ids=" + Uri.EscapeDataString(list)));
            }),
        new(
            "save_memory",
            "Save something to Engram's memory of this repository, when the user explicitly asks you to remember it. Write it as a plain fact, one to three sentences.",
            $$"""
            {"type":"object","properties":{
              "text":{"type":"string","description":"What to remember, as a plain statement of fact."},
              "title":{"type":"string","description":"Optional short title (12 words or fewer)."},
              "type":{"type":"string","enum":{{TypeEnum}},"description":"Default 'discovery'."},
              "files":{"type":"array","items":{"type":"string"},"description":"Related file paths."}
            },"required":["text"],"additionalProperties":false}
            """,
            args =>
            {
                using var buffer = new MemoryStream();
                using (var w = new Utf8JsonWriter(buffer))
                {
                    w.WriteStartObject();
                    w.WriteString("text", Required(args, "text"));
                    w.WriteString("cwd", cwd());
                    if (Str(args, "title") is { } title)
                    {
                        w.WriteString("title", title);
                    }

                    if (Str(args, "type") is { } type)
                    {
                        w.WriteString("type", type);
                    }

                    if (args.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    {
                        w.WritePropertyName("files");
                        files.WriteTo(w);
                    }

                    w.WriteEndObject();
                }

                return Relay(api.Post("/memory/save", buffer.ToArray()));
            }),
        new(
            "forget",
            "Delete one observation from Engram's memory, when the user asks you to or it is wrong.",
            """
            {"type":"object","properties":{
              "id":{"type":"integer","description":"Observation id to delete."}
            },"required":["id"],"additionalProperties":false}
            """,
            args => Relay(api.Post("/memory/forget?id=" + RequiredInt(args, "id").ToString(CultureInfo.InvariantCulture), []))),
    ];

    /// <summary>The worker answers in text; 2xx is a result, anything else a tool error.</summary>
    private static ToolResult Relay((int? Status, string? Body) response) => response switch
    {
        ( >= 200 and < 300, var body) => new ToolResult(body ?? string.Empty),
        (null, var body) => new ToolResult(body ?? "The Engram worker isn't reachable.", IsError: true),
        (401, _) => new ToolResult("The Engram worker rejected the token in ~/.engram/token. Restart it with `engram worker stop`.", IsError: true),
        (var status, var body) => new ToolResult(string.IsNullOrWhiteSpace(body) ? $"Engram worker error (HTTP {status})." : body.Trim(), IsError: true),
    };

    private static void Append(StringBuilder query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static string Required(JsonElement args, string name) => Str(args, name) ?? throw new ArgumentException($"{name} is required.");

    private static long? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
            : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString()?.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s
            : null
            : null;

    private static long RequiredInt(JsonElement args, string name) => Int(args, name) ?? throw new ArgumentException($"{name} is required (a number).");

    private static bool? Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
