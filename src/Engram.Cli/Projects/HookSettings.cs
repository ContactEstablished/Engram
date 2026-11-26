using System.Text.Json.Nodes;

namespace Engram.Cli.Projects;

/// <summary>
/// Engram's hook registrations as data, and the merge that puts them into a project's
/// <c>.claude/settings.local.json</c> without disturbing anything else in it. Engram's own entries are found by
/// shape (our command, or a loopback <c>/hooks/</c> URL with the ENGRAM_TOKEN header), so re-running enable
/// replaces them and disable removes exactly them.
/// </summary>
internal static class HookSettings
{
    /// <summary>The SessionStart command hook, and the HTTP endpoints Claude Code posts the other events to.</summary>
    private static readonly (string Event, string? Matcher, string? Path, int Timeout)[] Plan =
    [
        ("SessionStart", "*", null, 10),
        ("UserPromptSubmit", null, "prompt", 5),
        ("PostToolUse", "{tools}", "tool", 5),
        ("PostToolUseFailure", "{tools}", "tool", 5),
        ("Stop", null, "stop", 5),
        ("PreCompact", "*", "precompact", 5),
        ("SessionEnd", "*", "session-end", 2),
    ];

    public static readonly string[] Events = [.. Plan.Select(p => p.Event)];

    /// <summary>Adds Engram's hooks to <paramref name="root"/> (replacing earlier Engram entries). Returns how many events were set.</summary>
    public static int Apply(JsonObject root, int port, string toolMatcher, string engramCommand = "engram")
    {
        ArgumentNullException.ThrowIfNull(root);
        Remove(root);
        var hooks = root["hooks"] as JsonObject ?? [];
        root["hooks"] = hooks;
        foreach (var (name, matcher, path, timeout) in Plan)
        {
            var groups = hooks[name] as JsonArray ?? [];
            hooks[name] = groups;
            var group = new JsonObject();
            if (matcher is not null)
            {
                group["matcher"] = matcher == "{tools}" ? toolMatcher : matcher;
            }

            group["hooks"] = new JsonArray(path is null ? CommandHook(engramCommand, timeout) : HttpHook(port, path, timeout));
            groups.Add((JsonNode)group); // the non-generic overload: Add<T> is not AOT-safe
        }

        return Plan.Length;
    }

    /// <summary>Removes Engram's hooks, leaving everything else. Returns how many hook entries were removed.</summary>
    public static int Remove(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root["hooks"] is not JsonObject hooks)
        {
            return 0;
        }

        var removed = 0;
        foreach (var name in hooks.Select(h => h.Key).ToList())
        {
            if (hooks[name] is not JsonArray groups)
            {
                continue;
            }

            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray inner)
                {
                    continue;
                }

                foreach (var hook in inner.OfType<JsonObject>().Where(IsEngram).ToList())
                {
                    inner.Remove(hook);
                    removed++;
                }

                if (inner.Count == 0)
                {
                    groups.Remove(group);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(name);
            }
        }

        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }

        return removed;
    }

    /// <summary>The port in Engram's HTTP hook URLs, or null when none is registered.</summary>
    public static int? RegisteredPort(JsonObject root)
    {
        foreach (var hook in AllHooks(root).Where(IsEngram))
        {
            if (hook["url"]?.GetValue<string>() is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.Port;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> RegisteredEvents(JsonObject root) =>
        [.. (root["hooks"] as JsonObject ?? []).Where(e => e.Value is JsonArray groups
            && groups.OfType<JsonObject>().Any(g => g["hooks"] is JsonArray h && h.OfType<JsonObject>().Any(IsEngram))).Select(e => e.Key)];

    internal static bool IsEngram(JsonObject hook)
    {
        if (hook["type"]?.GetValue<string>() == "command")
        {
            return hook["command"]?.GetValue<string>() is { } c
                   && Path.GetFileNameWithoutExtension(c).Equals("engram", StringComparison.OrdinalIgnoreCase)
                   && hook["args"] is JsonArray { Count: >= 1 } args
                   && args[0]?.GetValue<string>() == "hook";
        }

        return hook["type"]?.GetValue<string>() == "http"
               && hook["url"]?.GetValue<string>() is { } url
               && url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
               && url.Contains("/hooks/", StringComparison.Ordinal)
               && hook["headers"]?.ToJsonString().Contains("ENGRAM_TOKEN", StringComparison.Ordinal) == true;
    }

    private static IEnumerable<JsonObject> AllHooks(JsonObject root) =>
        (root["hooks"] as JsonObject ?? [])
            .Select(e => e.Value as JsonArray)
            .OfType<JsonArray>()
            .SelectMany(g => g.OfType<JsonObject>())
            .Select(g => g["hooks"] as JsonArray)
            .OfType<JsonArray>()
            .SelectMany(h => h.OfType<JsonObject>());

    private static JsonObject CommandHook(string command, int timeout) => new()
    {
        ["type"] = "command",
        ["command"] = command,
        ["args"] = new JsonArray("hook", "session-start"),
        ["timeout"] = timeout,
    };

    private static JsonObject HttpHook(int port, string path, int timeout) => new()
    {
        ["type"] = "http",
        ["url"] = $"http://127.0.0.1:{port}/hooks/{path}",
        ["timeout"] = timeout,
        ["headers"] = new JsonObject
        {
            ["Authorization"] = "Bearer $ENGRAM_TOKEN",
            ["X-Engram-Disable"] = "${ENGRAM_DISABLE}",
        },
        ["allowedEnvVars"] = new JsonArray("ENGRAM_TOKEN", "ENGRAM_DISABLE"),
    };
}
