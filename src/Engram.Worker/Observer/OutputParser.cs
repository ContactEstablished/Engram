using System.Text.Json;
using Engram.Core;

namespace Engram.Worker.Observer;

/// <summary>
/// Reads the summarizer's reply. Models wrap JSON in fences or add a sentence around it, so the object is
/// located first; then the shape is checked and lengths are clamped. Any structural problem comes back as
/// an error string, which triggers one repair attempt.
/// </summary>
internal static class OutputParser
{
    public const int MaxObservations = 4;
    private const int MaxTitle = 120;
    private const int MaxNarrative = 700;
    private const int MaxFact = 200;
    private const int MaxFactCount = 8;
    private const int MaxPath = 260;
    private const int MaxPathCount = 12;
    private const int MaxSummaryField = 600;

    public static (IReadOnlyList<Observation>? Observations, string? Error) ParseObservations(string text)
    {
        if (!TryParseRoot(text, out var doc, out var error))
        {
            return (null, error);
        }

        using (doc)
        {
            var root = doc.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("observations", out array) && array.ValueKind == JsonValueKind.Array)
            {
                // ok
            }
            else
            {
                return (null, "expected an object with an \"observations\" array");
            }

            var result = new List<Observation>();
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return (null, $"observation {index} is not an object");
                }

                var type = ObservationTypes.Normalize(Str(item, "type"));
                if (type is null)
                {
                    return (null, $"observation {index} has type '{Str(item, "type")}'; allowed: {string.Join(", ", ObservationTypes.All)}");
                }

                var title = Clean(Str(item, "title"), MaxTitle);
                var narrative = Clean(Str(item, "narrative"), MaxNarrative);
                if (title.Length == 0 || narrative.Length == 0)
                {
                    return (null, $"observation {index} needs a non-empty title and narrative");
                }

                result.Add(new Observation(type, title, narrative, Strings(item, "facts", MaxFact, MaxFactCount), Strings(item, "files", MaxPath, MaxPathCount)));
            }

            return (result.Take(MaxObservations).ToList(), null);
        }
    }

    /// <summary>A note's summary: the same shape as a turn's observations, exactly one of them.</summary>
    public static (Observation? Summary, string? Error) ParseNote(string text)
    {
        var (observations, error) = ParseObservations(text);
        return observations switch
        {
            null => (null, error),
            [] => (null, "expected exactly one observation summarizing the note, got none"),
            [var first, ..] => (first, null),
        };
    }

    public static (SessionSummary? Summary, string? Error) ParseSummary(string text)
    {
        if (!TryParseRoot(text, out var doc, out var error))
        {
            return (null, error);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "expected a JSON object");
            }

            string[] names = ["request", "investigated", "learned", "completed", "next_steps"];
            foreach (var name in names)
            {
                if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    return (null, $"missing string field \"{name}\"");
                }
            }

            string F(string name) => Clean(Str(root, name), MaxSummaryField);
            return (new SessionSummary(F("request"), F("investigated"), F("learned"), F("completed"), F("next_steps")), null);
        }
    }

    /// <summary>Finds the outermost JSON object or array in <paramref name="text"/>, ignoring fences and chatter.</summary>
    private static bool TryParseRoot(string text, out JsonDocument doc, out string? error)
    {
        doc = null!;
        var start = text.IndexOfAny(['{', '[']);
        var end = text.LastIndexOfAny(['}', ']']);
        if (start < 0)
        {
            error = "the reply contained no JSON";
            return false;
        }

        if (end <= start)
        {
            error = "the reply was not valid JSON (it looks cut off)";
            return false;
        }

        try
        {
            doc = JsonDocument.Parse(text.AsMemory(start, end - start + 1), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = "the reply was not valid JSON (" + ex.Message.Split('.')[0] + ")";
            return false;
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[] Strings(JsonElement obj, string name, int maxLength, int maxCount) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => Clean(e.GetString(), maxLength)).Where(s => s.Length > 0).Take(maxCount)]
            : [];

    private static string Clean(string? value, int max)
    {
        var s = (value ?? string.Empty).Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
