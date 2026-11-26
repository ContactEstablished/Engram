using System.Globalization;
using System.Text.Json;
using Engram.Cli.Worker;
using Engram.Core;
using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Cli.Commands;

/// <summary>
/// <c>engram recent [--limit N]</c>: the latest observations and session summaries, newest first. This is the
/// quick way to judge memory quality before search and injection exist (Phase 3).
/// </summary>
internal static class RecentCommand
{
    public static int Run(string[] args, Func<string, string?> getEnv, TextWriter output) => Run(Parse(args), getEnv, output);

    internal static int Parse(string[] args)
    {
        var limit = 10;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--limit" or "-n" && i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                limit = Math.Clamp(n, 1, 100);
            }
            else
            {
                throw new ArgumentException($"recent: unknown option '{args[i]}'");
            }
        }

        return limit;
    }

    private static int Run(int limit, Func<string, string?> getEnv, TextWriter output)
    {
        var home = EngramPaths.Home(getEnv);
        var (settings, _) = SettingsLoader.Load(home, getEnv);
        var token = WorkerToken.Read(home);
        if (token is null)
        {
            output.WriteLine("No token yet. Run `engram install`.");
            return 1;
        }

        var (status, body) = WorkerClient.Get(settings.Worker.Port, $"/recent?limit={limit}", TimeSpan.FromSeconds(3), token);
        if (status != 200 || body is null)
        {
            output.WriteLine(status is null ? "The worker isn't running. Start it with `engram worker start`." : $"The worker answered HTTP {status}.");
            return 1;
        }

        try
        {
            Print(body, output);
        }
        catch (JsonException)
        {
            output.WriteLine("The worker's reply was unreadable; is the worker newer than this engram?");
            return 1;
        }

        return 0;
    }

    internal static void Print(string json, TextWriter output)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var any = false;
        if (root.TryGetProperty("summaries", out var summaries) && summaries.GetArrayLength() > 0)
        {
            any = true;
            output.WriteLine("SESSION SUMMARIES");
            foreach (var s in summaries.EnumerateArray())
            {
                output.WriteLine($"  {Str(s, "created_at")[..Math.Min(10, Str(s, "created_at").Length)]}  {Str(s, "project")} ({Str(s, "branch")})  [{Str(s, "kind")}]");
                Field(output, "request", s);
                Field(output, "investigated", s);
                Field(output, "learned", s);
                Field(output, "completed", s);
                Field(output, "next_steps", s);
            }

            output.WriteLine();
        }

        if (root.TryGetProperty("observations", out var observations) && observations.GetArrayLength() > 0)
        {
            any = true;
            output.WriteLine("OBSERVATIONS");
            foreach (var o in observations.EnumerateArray())
            {
                var created = Str(o, "created_at");
                output.WriteLine($"  #{o.GetProperty("id").GetInt64()} {Str(o, "type"),-9} {created[..Math.Min(10, created.Length)]}  {Str(o, "title")}   ({Str(o, "project")}, {Str(o, "branch")})");
                output.WriteLine($"      {Str(o, "narrative")}");
                var files = Str(o, "files");
                if (files is not ("" or "[]"))
                {
                    output.WriteLine($"      files: {files}");
                }
            }
        }

        if (!any)
        {
            output.WriteLine("Nothing yet. Observations appear after a turn finishes (Stop) and the observer has run; see `engram status`.");
        }
    }

    private static void Field(TextWriter output, string name, JsonElement s)
    {
        var value = Str(s, name);
        if (value.Length > 0)
        {
            output.WriteLine($"      {name}: {value}");
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
