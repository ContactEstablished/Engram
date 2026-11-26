using System.Text;
using System.Text.Json;

namespace Engram.Cli.Mcp;

/// <summary>What a tool call produced: text for Claude, and whether it is an error.</summary>
internal sealed record ToolResult(string Text, bool IsError = false);

/// <summary>A tool the server offers: name, description, JSON Schema for its arguments, and the handler.</summary>
internal sealed record McpTool(string Name, string Description, string InputSchemaJson, Func<JsonElement, ToolResult> Handle);

/// <summary>
/// A minimal MCP server over stdio (newline-delimited JSON-RPC 2.0): <c>initialize</c>, <c>tools/list</c>,
/// <c>tools/call</c>, <c>ping</c>. Hand-written rather than the SDK so the CLI stays dependency-free and
/// Native AOT. Nothing but protocol messages is ever written to stdout; diagnostics go to stderr.
/// </summary>
internal sealed class McpServer(IReadOnlyList<McpTool> tools, string version, string instructions)
{
    public const string LatestProtocolVersion = "2025-11-25";

    public void Run(Stream stdin, Stream stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        using var reader = new StreamReader(stdin, new UTF8Encoding(false));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            byte[]? response;
            try
            {
                response = Handle(line);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"engram mcp: {ex.GetType().Name}: {ex.Message}");
                response = Error(null, -32603, "Internal error");
            }

            if (response is not null)
            {
                stdout.Write(response);
                stdout.WriteByte((byte)'\n');
                stdout.Flush();
            }
        }
    }

    /// <summary>Handles one message; returns the response bytes, or null for notifications.</summary>
    internal byte[]? Handle(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                // A response from the client, or junk: nothing to answer.
                return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var badId) && !root.TryGetProperty("result", out _) && !root.TryGetProperty("error", out _)
                    ? Error(badId, -32600, "Invalid request")
                    : null;
            }

            var method = methodElement.GetString();
            var hasId = root.TryGetProperty("id", out var id);
            var @params = root.TryGetProperty("params", out var p) ? p : default;
            if (!hasId)
            {
                return null; // notifications: initialized, cancelled, ...
            }

            return method switch
            {
                "initialize" => Result(id, w => Initialize(w, @params)),
                "ping" => Result(id, EmptyObject),
                "tools/list" => Result(id, ListTools),
                "tools/call" => CallTool(id, @params),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
    }

    private void Initialize(Utf8JsonWriter w, JsonElement @params)
    {
        // Answer with the client's version: the tools surface used here is the same in every revision so far.
        var requested = @params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
        w.WriteStartObject();
        w.WriteString("protocolVersion", requested ?? LatestProtocolVersion);
        w.WriteStartObject("capabilities");
        w.WriteStartObject("tools");
        w.WriteBoolean("listChanged", false);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteStartObject("serverInfo");
        w.WriteString("name", "engram");
        w.WriteString("version", version);
        w.WriteEndObject();
        w.WriteString("instructions", instructions);
        w.WriteEndObject();
    }

    private void ListTools(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("tools");
        foreach (var tool in tools)
        {
            w.WriteStartObject();
            w.WriteString("name", tool.Name);
            w.WriteString("description", tool.Description);
            // Re-serialized compactly: stdio MCP is one message per line, so no raw newlines may reach stdout.
            w.WritePropertyName("inputSchema");
            using (var schema = JsonDocument.Parse(tool.InputSchemaJson))
            {
                schema.RootElement.WriteTo(w);
            }
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private byte[] CallTool(JsonElement id, JsonElement @params)
    {
        var name = @params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var tool = tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            return Error(id, -32602, $"Unknown tool: {name}");
        }

        var args = @params.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        ToolResult result;
        try
        {
            result = tool.Handle(args);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or JsonException)
        {
            result = new ToolResult(ex.Message, IsError: true);
        }

        return Result(id, w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", result.Text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("isError", result.IsError);
            w.WriteEndObject();
        });
    }

    private static void EmptyObject(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteEndObject();
    }

    private static byte[] Result(JsonElement id, Action<Utf8JsonWriter> writeResult) => Envelope(id, w =>
    {
        w.WritePropertyName("result");
        writeResult(w);
    });

    private static byte[] Error(JsonElement? id, int code, string message) => Envelope(id, w =>
    {
        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    private static byte[] Envelope(JsonElement? id, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            if (id is { } value && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                value.WriteTo(w);
            }
            else
            {
                w.WriteNullValue();
            }

            body(w);
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
