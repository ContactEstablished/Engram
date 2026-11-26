using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Engram.Core.Hooks;

/// <summary>
/// Reads a hook payload into its typed model by looking at <c>hook_event_name</c> first. This avoids
/// STJ polymorphism, which needs the discriminator to come first and throws on unknown values.
/// </summary>
public static class HookPayloadReader
{
    private const string EventNameProperty = "hook_event_name";

    /// <summary>Returns the top-level <c>hook_event_name</c>, or null if missing or not a string.</summary>
    /// <exception cref="JsonException">The input is not valid JSON.</exception>
    public static string? PeekEventName(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isEventName = reader.ValueTextEquals(EventNameProperty);
            reader.Read();
            if (isEventName)
            {
                return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }

            reader.Skip();
        }

        return null;
    }

    /// <summary>The typed model for an event, or <see cref="UnknownHookPayload"/>'s for anything else.</summary>
    public static JsonTypeInfo TypeInfoFor(string? eventName) => eventName switch
    {
        HookEvents.SessionStart => HookJsonContext.Default.SessionStartPayload,
        HookEvents.UserPromptSubmit => HookJsonContext.Default.UserPromptSubmitPayload,
        HookEvents.PreToolUse => HookJsonContext.Default.PreToolUsePayload,
        HookEvents.PostToolUse => HookJsonContext.Default.PostToolUsePayload,
        HookEvents.PostToolUseFailure => HookJsonContext.Default.PostToolUseFailurePayload,
        HookEvents.PostToolBatch => HookJsonContext.Default.PostToolBatchPayload,
        HookEvents.Stop => HookJsonContext.Default.StopPayload,
        HookEvents.SubagentStop => HookJsonContext.Default.SubagentStopPayload,
        HookEvents.PreCompact => HookJsonContext.Default.PreCompactPayload,
        HookEvents.PostCompact => HookJsonContext.Default.PostCompactPayload,
        HookEvents.SessionEnd => HookJsonContext.Default.SessionEndPayload,
        _ => HookJsonContext.Default.UnknownHookPayload,
    };

    /// <summary>Deserializes a payload into its typed model.</summary>
    /// <exception cref="JsonException">Invalid JSON, a missing required field, or a type mismatch.</exception>
    public static HookPayload Read(ReadOnlySpan<byte> utf8Json)
    {
        var eventName = PeekEventName(utf8Json)
            ?? throw new JsonException($"Hook payload has no string '{EventNameProperty}'.");

        var payload = JsonSerializer.Deserialize(utf8Json, TypeInfoFor(eventName)) as HookPayload;
        return payload ?? throw new JsonException("Hook payload deserialized to null.");
    }
}
