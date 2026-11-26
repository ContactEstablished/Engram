using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Engram.Core.Security;

namespace Engram.Worker.Capture;

/// <summary>
/// Makes tool input/output safe and small before it touches the database: drops whole-file copies,
/// redacts every string, and truncates long strings.
/// </summary>
internal sealed class PayloadShaper(Redactor redactor, int maxStringBytes, int maxPromptBytes)
{
    // Whole-file copies that only duplicate what's on disk (Phase 0 §11).
    private static readonly string[] DroppedResponsePaths = ["originalFile", "file.content"];

    public string? ShapeInput(JsonElement? input) => input is { } e ? Shape(JsonNode.Parse(e.GetRawText()), []) : null;

    public string? ShapeResponse(JsonElement? response) => response is { } e ? Shape(JsonNode.Parse(e.GetRawText()), DroppedResponsePaths) : null;

    /// <summary>For tool input/output and error text: redacted, then cut to <c>capture.maxStringBytes</c>.</summary>
    public string ShapeText(string text) => Truncate(redactor.Redact(text));

    /// <summary>For prompts and Claude's final answers: redacted, then cut to the larger <c>capture.maxPromptBytes</c>.</summary>
    public string ShapeLongText(string text) => Truncate(redactor.Redact(text), maxPromptBytes);

    private string? Shape(JsonNode? node, string[] dropPaths)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            foreach (var path in dropPaths)
            {
                Remove(obj, path.Split('.'));
            }
        }

        return Walk(node)?.ToJsonString(Options);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        // Keep non-ASCII and <, > readable in the database; it is never rendered as HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void Remove(JsonObject obj, string[] path)
    {
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (obj[path[i]] is not JsonObject child)
            {
                return;
            }

            obj = child;
        }

        if (obj[path[^1]] is not null)
        {
            obj[path[^1]] = "<omitted>";
        }
    }

    /// <summary>Rewrites string values in place; returns the (possibly replaced) node.</summary>
    private JsonNode? Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (IsString(obj[key]))
                    {
                        obj[key] = Walk(obj[key]);
                    }
                    else
                    {
                        Walk(obj[key]);
                    }
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (IsString(array[i]))
                    {
                        array[i] = Walk(array[i]);
                    }
                    else
                    {
                        Walk(array[i]);
                    }
                }

                return array;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(ShapeText(value.GetValue<string>()));
            default:
                return node;
        }
    }

    private static bool IsString(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    /// <summary>
    /// Keeps the start and the end of <paramref name="value"/>, within <paramref name="limitBytes"/> UTF-8 bytes
    /// (a quarter for the start, the rest for the end), and marks the cut. Build and test logs put the
    /// verdict at the end, so a head-only cut would keep the chatter and lose the failure.
    /// </summary>
    public static string Truncate(string value, int limitBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) <= limitBytes)
        {
            return value;
        }

        var headBudget = limitBytes / 4;
        var tailBudget = limitBytes - headBudget;
        var head = 0;
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > headBudget)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            head += rune.Utf16SequenceLength;
        }

        var tailStart = value.Length;
        bytes = 0;
        for (var i = value.Length; i > head;)
        {
            var width = char.IsLowSurrogate(value[i - 1]) && i - 2 >= head && char.IsHighSurrogate(value[i - 2]) ? 2 : 1;
            var runeBytes = width == 2 ? 4 : Encoding.UTF8.GetByteCount(value.AsSpan(i - width, width));
            if (bytes + runeBytes > tailBudget)
            {
                break;
            }

            bytes += runeBytes;
            i -= width;
            tailStart = i;
        }

        return string.Concat(value.AsSpan(0, head), $"…<truncated {tailStart - head} chars>…", value.AsSpan(tailStart));
    }

    private string Truncate(string value) => Truncate(value, maxStringBytes);
}
