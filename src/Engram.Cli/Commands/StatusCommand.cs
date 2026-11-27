using Engram.Cli.Worker;
using Engram.Core;
using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Cli.Commands;

/// <summary><c>engram status</c> and <c>engram worker start</c>: is the worker up, and where is everything.</summary>
internal static class StatusCommand
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static int Run(Func<string, string?> getEnv, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var home = EngramPaths.Home(getEnv);
        var (settings, warnings) = SettingsLoader.Load(home, getEnv);
        var token = WorkerToken.Read(home);
        var (status, body) = WorkerClient.Get(settings.Worker.Port, "/health", Timeout);
        var spooled = Directory.Exists(EngramPaths.Spool(home)) ? Directory.GetFiles(EngramPaths.Spool(home), "*.json").Length : 0;
        var spooledNotes = Directory.Exists(EngramPaths.NoteSpool(home)) ? Directory.GetFiles(EngramPaths.NoteSpool(home), "*.json").Length : 0;

        output.WriteLine($"home:          {home}");
        output.WriteLine($"worker:        {(status == 200 ? $"running on 127.0.0.1:{settings.Worker.Port} {body}" : $"not running (port {settings.Worker.Port})")}");
        output.WriteLine($"worker binary: {WorkerLauncher.Locate(getEnv) ?? "NOT FOUND"}");
        output.WriteLine($"database:      {(File.Exists(EngramPaths.Database(home)) ? EngramPaths.Database(home) : "not created yet")}");
        output.WriteLine($"token:         {(token is null ? "missing (run engram install)" : "present")}");
        output.WriteLine($"ENGRAM_TOKEN:  {(getEnv(WorkerToken.EnvironmentVariable) is { } env ? env == token ? "set, matches" : "set, DOES NOT MATCH ~/.engram/token" : "not set in this shell")}");
        output.WriteLine($"spooled:       {spooled} payload(s) and {spooledNotes} note(s) waiting for the worker");
        if (status == 200 && token is not null && WorkerClient.Get(settings.Worker.Port, "/stats", Timeout, token) is (200, { } stats))
        {
            try
            {
                PrintStats(stats, output);
            }
            catch (System.Text.Json.JsonException)
            {
                output.WriteLine("stored:        (the worker's stats were unreadable; is the worker newer than this engram?)");
            }
        }

        foreach (var warning in warnings)
        {
            output.WriteLine($"warning:       {warning}");
        }

        return status == 200 ? 0 : 1;
    }

    /// <summary>Prints the worker's /stats JSON as a few readable lines. Tolerates missing fields (older workers).</summary>
    internal static void PrintStats(string json, TextWriter output)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        long N(System.Text.Json.JsonElement parent, string name) =>
            parent.ValueKind == System.Text.Json.JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
        var counts = root.TryGetProperty("counts", out var c) ? c : default;
        output.WriteLine($"stored:        {N(counts, "sessions")} sessions, {N(counts, "prompts")} prompts, {N(counts, "events")} tool events, {N(counts, "observations")} observations, {N(counts, "summaries")} summaries");
        if (root.TryGetProperty("jobs", out var jobs))
        {
            output.WriteLine($"jobs:          {N(jobs, "pending")} pending, {N(jobs, "running")} running, {N(jobs, "done")} done, {N(jobs, "parked")} parked");
            if (jobs.TryGetProperty("last_error", out var err) && err.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                output.WriteLine($"last error:    {err.GetString()}");
            }
        }

        if (root.TryGetProperty("search", out var search))
        {
            var semantic = search.TryGetProperty("semantic", out var on) && on.ValueKind == System.Text.Json.JsonValueKind.True;
            var model = search.TryGetProperty("model", out var m2) ? m2.GetString() : "?";
            var error = search.TryGetProperty("last_error", out var e2) && e2.ValueKind == System.Text.Json.JsonValueKind.String ? e2.GetString() : null;
            output.WriteLine(!semantic
                ? "search:        keyword only (search.semantic = false)"
                : $"search:        keyword + meaning ({model}), {N(search, "vectors")} of {N(counts, "observations")} observations embedded{(error is null ? string.Empty : $"; embeddings unavailable: {error}")}");
        }

        if (root.TryGetProperty("observer", out var obs))
        {
            var provider = obs.TryGetProperty("provider", out var p) ? p.GetString() : "?";
            var model = obs.TryGetProperty("model", out var m) ? m.GetString() : "?";
            var cost = obs.TryGetProperty("cost_usd_24h", out var usd) && usd.TryGetDouble(out var d) ? d : 0;
            output.WriteLine(provider == "none"
                ? "observer:      off (observer.provider = none); jobs wait in the queue"
                : $"observer:      {provider}/{model}, last 24h: {N(obs, "calls_24h")} calls ({N(obs, "failed_24h")} failed), {N(obs, "input_tokens_24h")} in / {N(obs, "output_tokens_24h")} out tokens, list-price cost ~${cost.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}; budget {N(obs, "max_calls_per_hour")}/hour");
        }
    }

    /// <summary>
    /// <c>engram status --json</c>: one JSON object for programs (Chorus). Always exits 0; whether the worker runs is
    /// in the JSON. Never prints the token.
    /// </summary>
    public static int RunJson(Func<string, string?> getEnv, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var home = EngramPaths.Home(getEnv);
        var (settings, warnings) = SettingsLoader.Load(home, getEnv);
        var port = settings.Worker.Port;
        var token = WorkerToken.Read(home);
        var (status, health) = WorkerClient.Get(port, "/health", Timeout);
        var running = status == 200;
        string? workerVersion = null;
        long? schema = null;
        if (running && health is not null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(health);
                workerVersion = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                schema = doc.RootElement.TryGetProperty("schema", out var s) && s.TryGetInt64(out var n) ? n : null;
            }
            catch (System.Text.Json.JsonException)
            {
                // An older worker: running, version unknown.
            }
        }

        long? observations = null, sessions = null, vectors = null;
        if (running && token is not null && WorkerClient.Get(port, "/stats", Timeout, token) is (200, { } stats))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(stats);
                var counts = doc.RootElement.TryGetProperty("counts", out var c) ? c : default;
                observations = counts.ValueKind == System.Text.Json.JsonValueKind.Object && counts.TryGetProperty("observations", out var o) ? o.GetInt64() : null;
                sessions = counts.ValueKind == System.Text.Json.JsonValueKind.Object && counts.TryGetProperty("sessions", out var se) ? se.GetInt64() : null;
                vectors = doc.RootElement.TryGetProperty("search", out var sr) && sr.TryGetProperty("vectors", out var ve) ? ve.GetInt64() : null;
            }
            catch (System.Text.Json.JsonException)
            {
                // Leave the counts null.
            }
        }

        var spooled = Directory.Exists(EngramPaths.Spool(home)) ? Directory.GetFiles(EngramPaths.Spool(home), "*.json").Length : 0;
        var spooledNotes = Directory.Exists(EngramPaths.NoteSpool(home)) ? Directory.GetFiles(EngramPaths.NoteSpool(home), "*.json").Length : 0;
        output.WriteLine(EnableCommand.Json(w =>
        {
            w.WriteString("version", typeof(StatusCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            w.WriteString("home", home);
            w.WriteStartObject("worker");
            w.WriteBoolean("running", running);
            w.WriteNumber("port", port);
            Nullable(w, "version", workerVersion);
            if (schema is { } sc)
            {
                w.WriteNumber("schema", sc);
            }
            else
            {
                w.WriteNull("schema");
            }

            Nullable(w, "binary", WorkerLauncher.Locate(getEnv));
            w.WriteEndObject();
            w.WriteBoolean("token_present", token is not null);
            w.WriteString("token_path", EngramPaths.TokenFile(home));
            w.WriteBoolean("token_in_environment", token is not null && getEnv(WorkerToken.EnvironmentVariable) == token);
            Count(w, "observations", observations);
            Count(w, "sessions", sessions);
            Count(w, "vectors", vectors);
            w.WriteNumber("spooled", spooled);
            w.WriteNumber("spooled_notes", spooledNotes);
            w.WriteStartArray("warnings");
            foreach (var warning in warnings)
            {
                w.WriteStringValue(warning);
            }

            w.WriteEndArray();
        }));
        return 0;

        static void Nullable(System.Text.Json.Utf8JsonWriter w, string name, string? value)
        {
            if (value is null)
            {
                w.WriteNull(name);
            }
            else
            {
                w.WriteString(name, value);
            }
        }

        static void Count(System.Text.Json.Utf8JsonWriter w, string name, long? value)
        {
            if (value is { } v)
            {
                w.WriteNumber(name, v);
            }
            else
            {
                w.WriteNull(name);
            }
        }
    }

    public static int StopWorker(Func<string, string?> getEnv, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var home = EngramPaths.Home(getEnv);
        var (settings, _) = SettingsLoader.Load(home, getEnv);
        var status = WorkerClient.Post(settings.Worker.Port, "/shutdown", WorkerToken.Read(home), [], Timeout);
        output.WriteLine(status switch
        {
            null => "Worker not running.",
            200 => "Worker stopping.",
            _ => $"Worker refused to stop (HTTP {status}); is ~/.engram/token the one it started with?",
        });
        return status is null or 200 ? 0 : 1;
    }

    public static int StartWorker(Func<string, string?> getEnv, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var home = EngramPaths.Home(getEnv);
        var (settings, _) = SettingsLoader.Load(home, getEnv);
        if (WorkerClient.Get(settings.Worker.Port, "/health", Timeout).Status == 200)
        {
            output.WriteLine("Worker already running.");
            return 0;
        }

        if (WorkerLauncher.Locate(getEnv) is not { } worker)
        {
            output.WriteLine($"engram-worker not found next to {AppContext.BaseDirectory}");
            return 1;
        }

        if (!WorkerLauncher.TryStart(worker, home, out var error))
        {
            output.WriteLine($"Could not start the worker: {error}");
            return 1;
        }

        for (var i = 0; i < 50; i++)
        {
            Thread.Sleep(100);
            if (WorkerClient.Get(settings.Worker.Port, "/health", Timeout).Status == 200)
            {
                output.WriteLine($"Worker started on 127.0.0.1:{settings.Worker.Port}.");
                return 0;
            }
        }

        output.WriteLine($"Worker didn't answer within 5 s; see {EngramPaths.Logs(home)}.");
        return 1;
    }
}
