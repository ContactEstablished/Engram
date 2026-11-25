using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Engram.Core;
using Engram.Core.Hooks;

namespace Engram.Cli.Spike;

/// <summary>
/// <c>engram spike fixtures</c>: turns captured JSONL into one sanitized fixture per event (per tool for
/// tool events, per source for SessionStart) and prints a field inventory for the findings doc.
/// </summary>
internal static class SpikeFixturesCommand
{
    private static readonly JsonWriterOptions s_indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal sealed record Options(IReadOnlyList<string> From, string Out, IReadOnlyList<string> Redact, int MaxStringLength);

    internal sealed record Captured(int SourceIndex, string Source, string Event, string Key, JsonElement Payload, JsonElement Wrapper, int Richness);

    public static int Run(string[] args)
    {
        var options = Parse(args);
        var literals = DefaultLiterals().Concat(options.Redact.Select(r => (r, "<REDACTED>")));
        return Run(options, literals, Console.Out);
    }

    internal static int Run(Options options, IEnumerable<(string Term, string Replacement)> literals, TextWriter report)
    {
        var records = new List<Captured>();
        for (var i = 0; i < options.From.Count; i++)
        {
            records.AddRange(ReadDir(i, options.From[i], report));
        }

        if (records.Count == 0)
        {
            report.WriteLine("No captures found. Run a Claude Code session with the spike hooks first (see spike/README.md).");
            return 1;
        }

        var selected = records
            .GroupBy(r => r.Key, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.Richness).ThenBy(r => r.SourceIndex).First())
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToList();

        // Learn usernames from home paths in the data (e.g. the WSL user), so bare mentions get replaced too.
        var learned = selected.SelectMany(r => FixtureSanitizer.FindUsernamesInPaths(r.Payload.GetRawText()))
            .Where(u => !u.StartsWith('<'))
            .Select(u => (u, FixtureSanitizer.User));
        var sanitizer = new FixtureSanitizer(literals.Concat(learned), options.MaxStringLength);

        Directory.CreateDirectory(options.Out);
        foreach (var record in selected)
        {
            var node = sanitizer.Sanitize(JsonNode.Parse(record.Payload.GetRawText()));
            var path = Path.Combine(options.Out, record.Key + ".json");
            using (var stream = File.Create(path))
            using (var writer = new Utf8JsonWriter(stream, s_indented))
            {
                (node ?? new JsonObject()).WriteTo(writer);
            }

            File.AppendAllText(path, "\n");
        }

        WriteReport(report, options, records, selected);
        return 0;
    }

    internal static Options Parse(string[] args)
    {
        var from = new List<string>();
        var redact = new List<string>();
        string? output = null;
        var maxString = 2000;
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"spike fixtures: {args[i]} needs a value.");
            switch (args[i])
            {
                case "--from": from.Add(value); break;
                case "--out": output = value; break;
                case "--redact": redact.Add(value); break;
                case "--max-string": maxString = int.Parse(value, CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"spike fixtures: unexpected argument '{args[i]}'.");
            }

            i++;
        }

        if (from.Count == 0)
        {
            from.Add(EngramPaths.SpikeDir(Environment.GetEnvironmentVariable));
        }

        output ??= FindRepoFixturesDir()
            ?? throw new ArgumentException("spike fixtures: run from inside the Engram repo or pass --out <dir>.");
        return new Options(from, output, redact, maxString);
    }

    internal static string KeyFor(string eventName, JsonElement payload)
    {
        var key = SpikeCapture.FileNameFor(eventName);
        string? suffix = null;
        if (HookEvents.IsToolEvent(eventName) && payload.TryGetProperty("tool_name", out var tool) && tool.ValueKind == JsonValueKind.String)
        {
            suffix = tool.GetString();
        }
        else if (eventName == HookEvents.SessionStart && payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String)
        {
            suffix = source.GetString();
        }

        return suffix is null ? key : $"{key}.{SpikeCapture.FileNameFor(suffix)}";
    }

    private static IEnumerable<Captured> ReadDir(int index, string dir, TextWriter report)
    {
        if (!Directory.Exists(dir))
        {
            report.WriteLine($"warning: {dir} does not exist; skipped.");
            yield break;
        }

        foreach (var file in Directory.GetFiles(dir, "*.jsonl").Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith("http.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonElement wrapper;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    wrapper = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                var payload = wrapper.TryGetProperty("payload", out var p) ? p : wrapper;
                if (payload.ValueKind != JsonValueKind.Object ||
                    !payload.TryGetProperty("hook_event_name", out var ev) || ev.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var eventName = ev.GetString()!;
                yield return new Captured(index, dir, eventName, KeyFor(eventName, payload), payload, wrapper, Richness(payload, 0));
            }
        }
    }

    /// <summary>Counts properties up to three levels deep: a rough "most complete example" score.</summary>
    private static int Richness(JsonElement element, int depth)
    {
        if (depth > 2)
        {
            return 0;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Sum(p => 1 + Richness(p.Value, depth + 1)),
            JsonValueKind.Array => element.EnumerateArray().Take(3).Sum(e => Richness(e, depth + 1)),
            _ => 0,
        };
    }

    private static string? FindRepoFixturesDir()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Engram.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "fixtures", "hooks");
            }
        }

        return null;
    }

    private static IEnumerable<(string, string)> DefaultLiterals()
    {
        yield return (Environment.UserName, FixtureSanitizer.User);
        yield return (Environment.MachineName, "<HOST>");
        if (Environment.GetEnvironmentVariable("ENGRAM_TOKEN") is { Length: > 0 } token)
        {
            yield return (token, FixtureSanitizer.Redacted);
        }

        if (GitUserName() is { } name)
        {
            yield return (name, "<NAME>");
        }
    }

    private static string? GitUserName()
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "config --get user.name")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (git is null)
            {
                return null;
            }

            var name = git.StandardOutput.ReadToEnd().Trim();
            return git.WaitForExit(2000) && git.ExitCode == 0 && name.Length > 0 ? name : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteReport(TextWriter w, Options options, List<Captured> all, List<Captured> selected)
    {
        var sources = options.From.Select((dir, i) => (dir, i)).ToList();
        w.WriteLine($"# Phase 0 capture report ({DateTimeOffset.Now:yyyy-MM-dd HH:mm})");
        w.WriteLine();
        foreach (var (dir, i) in sources)
        {
            var mine = all.Where(r => r.SourceIndex == i).ToList();
            var os = mine.Select(r => Str(r.Wrapper, "os") + (Bool(r.Wrapper, "is_wsl") ? " (WSL)" : "")).Distinct();
            w.WriteLine($"- source {i}: {dir} — {mine.Count} records, os: {string.Join(", ", os)}");
        }

        w.WriteLine();
        w.WriteLine($"Wrote {selected.Count} fixtures to {options.Out}:");
        foreach (var r in selected)
        {
            w.WriteLine($"  {r.Key}.json  (from source {r.SourceIndex})");
        }

        foreach (var group in all.GroupBy(r => r.Event).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            w.WriteLine();
            w.WriteLine($"## {group.Key}");
            w.WriteLine($"records: {group.Count()} ({string.Join(", ", sources.Select(s => $"source {s.i}: {group.Count(r => r.SourceIndex == s.i)}"))})");

            var fieldsBySource = sources.ToDictionary(
                s => s.i,
                s => group.Where(r => r.SourceIndex == s.i).SelectMany(r => r.Payload.EnumerateObject().Select(p => p.Name)).ToHashSet(StringComparer.Ordinal));
            var union = fieldsBySource.Values.SelectMany(f => f).ToHashSet(StringComparer.Ordinal);
            w.WriteLine($"fields: {string.Join(", ", union.Order(StringComparer.Ordinal))}");
            foreach (var (i, fields) in fieldsBySource.Where(kv => kv.Value.Count > 0))
            {
                var missingHere = union.Except(fields).Order(StringComparer.Ordinal).ToList();
                if (missingHere.Count > 0 && fieldsBySource.Count(kv => kv.Value.Count > 0) > 1)
                {
                    w.WriteLine($"  not seen in source {i}: {string.Join(", ", missingHere)}");
                }
            }

            WriteTimings(w, group);

            if (group.Key == HookEvents.SessionStart)
            {
                w.WriteLine($"sources: {Counts(group.Select(r => Str(r.Payload, "source")))}");
            }
            else if (group.Key is HookEvents.PreCompact or HookEvents.PostCompact)
            {
                w.WriteLine($"triggers: {Counts(group.Select(r => Str(r.Payload, "trigger")))}");
            }
            else if (group.Key == HookEvents.SessionEnd)
            {
                w.WriteLine($"reasons: {Counts(group.Select(r => Str(r.Payload, "reason")))}");
            }
            else if (HookEvents.IsToolEvent(group.Key))
            {
                foreach (var tool in group.GroupBy(r => Str(r.Payload, "tool_name")).OrderBy(t => t.Key, StringComparer.Ordinal))
                {
                    w.WriteLine($"  tool {tool.Key} ({tool.Count()}):");
                    w.WriteLine($"    tool_input: {Shape(tool.Select(r => r.Payload), "tool_input")}");
                    if (group.Key == HookEvents.PostToolUse)
                    {
                        w.WriteLine($"    tool_response: {Shape(tool.Select(r => r.Payload), "tool_response")}");
                    }
                }
            }
            else if (group.Key == HookEvents.PostToolBatch)
            {
                var calls = group.SelectMany(r => r.Payload.TryGetProperty("tool_calls", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray() : []).ToList();
                w.WriteLine($"  tool_calls: {calls.Count} total; item fields: {string.Join(", ", calls.SelectMany(c => c.ValueKind == JsonValueKind.Object ? c.EnumerateObject().Select(p => p.Name) : []).Distinct().Order(StringComparer.Ordinal))}");
                w.WriteLine($"  tool_calls[].tool_response kinds: {Counts(calls.Select(c => c.TryGetProperty("tool_response", out var tr) ? tr.ValueKind.ToString() : "absent"))}");
            }
        }

        WriteHttpSummary(w, options.From);
    }

    private static void WriteTimings(TextWriter w, IEnumerable<Captured> group)
    {
        foreach (var bySource in group.GroupBy(r => r.SourceIndex))
        {
            var handler = bySource.Select(r => Num(r.Wrapper, "handler_ms")).OfType<double>().Order().ToList();
            var process = bySource.Select(r => Num(r.Wrapper, "process_ms")).OfType<double>().Order().ToList();
            if (process.Count > 0)
            {
                w.WriteLine($"  timings source {bySource.Key}: process_ms p50 {P(process, 0.5)} p95 {P(process, 0.95)} max {process[^1]:0.#}; handler_ms p50 {P(handler, 0.5)} p95 {P(handler, 0.95)}");
            }
        }
    }

    private static void WriteHttpSummary(TextWriter w, IReadOnlyList<string> dirs)
    {
        w.WriteLine();
        w.WriteLine("## HTTP hook requests (spike listen)");
        var any = false;
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.GetFiles(dir, "http.*.jsonl").Order(StringComparer.Ordinal))
            {
                any = true;
                var lines = File.ReadLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
                var matches = Counts(lines.Select(l => l.TryGetProperty("authorization", out var a) && a.TryGetProperty("matches_env", out var m) ? m.ToString() : "?"));
                var headerNames = lines.SelectMany(l => l.TryGetProperty("headers", out var h) ? h.EnumerateObject().Select(p => p.Name) : []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
                w.WriteLine($"- {file}: {lines.Count} requests; authorization.matches_env: {matches}");
                w.WriteLine($"  headers seen: {string.Join(", ", headerNames)}");
                w.WriteLine($"  X-Engram-Disable values: {Counts(lines.Select(l => HeaderValue(l, "X-Engram-Disable")))}");
                w.WriteLine($"  X-Engram-Unlisted values: {Counts(lines.Select(l => HeaderValue(l, "X-Engram-Unlisted")))}");
            }
        }

        if (!any)
        {
            w.WriteLine("(none captured)");
        }
    }

    private static string HeaderValue(JsonElement line, string name)
    {
        if (line.TryGetProperty("headers", out var headers))
        {
            foreach (var p in headers.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return $"'{p.Value.GetString()}'";
                }
            }
        }

        return "absent";
    }

    private static string Shape(IEnumerable<JsonElement> payloads, string property)
    {
        var shapes = payloads.Select(p =>
        {
            if (!p.TryGetProperty(property, out var v))
            {
                return "absent";
            }

            return v.ValueKind == JsonValueKind.Object
                ? "{" + string.Join(", ", v.EnumerateObject().Select(o => $"{o.Name}:{Kind(o.Value)}")) + "}"
                : Kind(v);
        });
        return Counts(shapes);
    }

    private static string Kind(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => e.ValueKind.ToString(),
    };

    private static string Counts(IEnumerable<string> values) =>
        string.Join("; ", values.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}"));

    private static string P(List<double> sorted, double q) =>
        sorted.Count == 0 ? "-" : sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)].ToString("0.#", CultureInfo.InvariantCulture);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "-";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
