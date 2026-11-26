using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Engram.Core.Security;

namespace Engram.Worker.Capture;

/// <summary>
/// Makes tool input/output safe and small before it touches the database: drops whole-file copies,
/// redacts every string, and truncates long strings.
/// </summary>
internal sealed class PayloadShaper(Redactor redactor, int maxStringBytes)
{
    // Whole-file copies that only duplicate what's on disk (Phase 0 §11).
    private static readonly string[] DroppedResponsePaths = ["originalFile", "file.content"];

    public string? ShapeInput(JsonElement? input) => input is { } e ? Shape(JsonNode.Parse(e.GetRawText()), []) : null;

    public string? ShapeResponse(JsonElement? response) => response is { } e ? Shape(JsonNode.Parse(e.GetRawText()), DroppedResponsePaths) : null;

    public string ShapeText(string text) => Truncate(redactor.Redact(text));

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

    /// <summary>Cuts to <c>maxStringBytes</c> UTF-8 bytes on a character boundary, with a marker.</summary>
    public string Truncate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) <= maxStringBytes)
        {
            return value;
        }

        var bytes = 0;
        var cut = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxStringBytes)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            cut += rune.Utf16SequenceLength;
        }

        return string.Concat(value.AsSpan(0, cut), $"…<truncated {value.Length - cut} chars>");
    }
}
