using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Engram.Cli.Mcp;

namespace Engram.Cli.Commands;

/// <summary>
/// <c>engram eval FILE [--k 5]</c>: the retrieval eval from the plan. Each question lists the observation ids that
/// answer it; every question runs through keyword, meaning and hybrid search, and the report shows recall@k for
/// each mode plus the questions each mode missed. File format:
/// <code>{ "k": 5, "cwd": "optional repo folder", "questions": [ { "q": "why webhooks?", "expected": [12, 15], "all_repos": false } ] }</code>
/// </summary>
internal static partial class EvalCommand
{
    private static readonly string[] Modes = ["keyword", "semantic", "hybrid"];

    internal sealed record Question(string Text, IReadOnlyList<long> Expected, bool AllRepos);

    internal sealed record Result(string Mode, double Recall, double HitRate, IReadOnlyList<(Question Question, IReadOnlyList<long> Found)> Misses);

    public static int Run(string[] args, IMemoryApi api, Func<string> cwd, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("eval: give a questions file, e.g. engram eval docs/retrieval-eval.json");
        }

        var (questions, k, fileCwd) = Load(File.ReadAllText(args[0]));
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--k" && i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                k = Math.Clamp(n, 1, 50);
            }
            else
            {
                throw new ArgumentException($"eval: unknown option '{args[i]}'");
            }
        }

        var results = Evaluate(questions, k, api, fileCwd ?? cwd());
        Print(results, questions.Count, k, output);
        return 0;
    }

    internal static (List<Question> Questions, int K, string? Cwd) Load(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var k = root.TryGetProperty("k", out var kv) && kv.TryGetInt32(out var kn) ? Math.Clamp(kn, 1, 50) : 5;
        var cwd = root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (!root.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("eval: the file needs a \"questions\" array.");
        }

        var questions = qs.EnumerateArray().Select((q, i) =>
        {
            var text = q.TryGetProperty("q", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : throw new ArgumentException($"eval: question {i + 1} has no \"q\".");
            var expected = q.TryGetProperty("expected", out var e) && e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetInt64()).ToList() : [];
            if (expected.Count == 0)
            {
                throw new ArgumentException($"eval: question {i + 1} (\"{text}\") has no expected ids.");
            }

            return new Question(text, expected, q.TryGetProperty("all_repos", out var a) && a.ValueKind == JsonValueKind.True);
        }).ToList();
        return (questions, k, cwd);
    }

    internal static List<Result> Evaluate(IReadOnlyList<Question> questions, int k, IMemoryApi api, string cwd)
    {
        var results = new List<Result>();
        foreach (var mode in Modes)
        {
            double recall = 0;
            var hits = 0;
            var misses = new List<(Question, IReadOnlyList<long>)>();
            foreach (var question in questions)
            {
                var path = $"/memory/search?q={Uri.EscapeDataString(question.Text)}&limit={k}&mode={mode}" +
                           (question.AllRepos ? "&all=1" : $"&cwd={Uri.EscapeDataString(cwd)}");
                var (status, body) = api.Get(path);
                if (status is not (>= 200 and < 300))
                {
                    throw new InvalidOperationException($"eval: search failed (HTTP {status?.ToString(CultureInfo.InvariantCulture) ?? "none"}): {body}");
                }

                var found = IdRegex().Matches(body ?? string.Empty).Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Take(k).ToList();
                var matched = question.Expected.Count(found.Contains);
                recall += (double)matched / question.Expected.Count;
                if (matched > 0)
                {
                    hits++;
                }

                if (matched < question.Expected.Count)
                {
                    misses.Add((question, found));
                }
            }

            results.Add(new Result(mode, recall / questions.Count, (double)hits / questions.Count, misses));
        }

        return results;
    }

    private static string Percent(double value) => (value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static void Print(List<Result> results, int count, int k, TextWriter output)
    {
        output.WriteLine($"Retrieval eval: {count} question(s), top {k}");
        output.WriteLine();
        output.WriteLine($"  {"mode",-9} {"recall@" + k,9} {"hit@" + k,7}");
        foreach (var r in results)
        {
            output.WriteLine($"  {r.Mode,-9} {Percent(r.Recall),9} {Percent(r.HitRate),7}");
        }

        foreach (var r in results.Where(r => r.Misses.Count > 0))
        {
            output.WriteLine();
            output.WriteLine($"Missed by {r.Mode}:");
            foreach (var (question, found) in r.Misses)
            {
                var missing = question.Expected.Except(found).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture));
                var got = found.Count == 0 ? "nothing" : string.Join(' ', found.Select(id => "#" + id.ToString(CultureInfo.InvariantCulture)));
                output.WriteLine($"  \"{question.Text}\": missing {string.Join(' ', missing)} (got {got})");
            }
        }
    }

    [GeneratedRegex(@"^\s*>?\s*#(\d+)\s", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();
}
