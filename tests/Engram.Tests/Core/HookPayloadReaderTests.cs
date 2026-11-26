using System.Text;
using System.Text.Json;
using Engram.Core.Hooks;

namespace Engram.Tests.Core;

public sealed class HookPayloadReaderTests
{
    private const string Common = """
        "session_id":"s1","transcript_path":"/t.jsonl","cwd":"/repo"
        """;

    private static HookPayload Read(string json) => HookPayloadReader.Read(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Reads_typed_model_when_event_name_is_not_first()
    {
        var payload = Read($$"""{"reason":"other",{{Common}},"hook_event_name":"SessionEnd"}""");

        var end = payload.ShouldBeOfType<SessionEndPayload>();
        end.Reason.ShouldBe("other");
        end.SessionId.ShouldBe("s1");
    }

    [Fact]
    public void Missing_required_field_throws()
    {
        // PostToolUse without tool_response
        var json = $$"""{"hook_event_name":"PostToolUse",{{Common}},"tool_name":"Bash","tool_input":{},"tool_use_id":"t1"}""";

        Should.Throw<JsonException>(() => Read(json)).Message.ShouldContain("tool_response");
    }

    [Fact]
    public void Null_in_non_nullable_field_throws()
    {
        var json = $$"""{"hook_event_name":"UserPromptSubmit",{{Common}},"prompt":null}""";

        Should.Throw<JsonException>(() => Read(json));
    }

    [Fact]
    public void Unknown_event_reads_as_UnknownHookPayload_with_extension_data()
    {
        var payload = Read($$"""{"hook_event_name":"Notification",{{Common}},"message":"hi"}""");

        payload.ShouldBeOfType<UnknownHookPayload>();
        payload.ExtensionData.ShouldNotBeNull().ShouldContainKey("message");
    }

    [Fact]
    public void Undeclared_fields_are_kept_in_extension_data()
    {
        var payload = Read($$"""{"hook_event_name":"SessionStart",{{Common}},"source":"startup","brand_new_field":42}""");

        payload.ShouldBeOfType<SessionStartPayload>().ExtensionData.ShouldNotBeNull().ShouldContainKey("brand_new_field");
    }

    [Fact]
    public void Missing_event_name_throws()
    {
        Should.Throw<JsonException>(() => Read($$"""{{{Common}}}"""));
    }

    [Theory]
    [InlineData("""{"a":{"hook_event_name":"Nested"},"hook_event_name":"Stop"}""", "Stop")]
    [InlineData("""{"hook_event_name":5}""", null)]
    [InlineData("""[1,2]""", null)]
    public void PeekEventName_reads_only_the_top_level_string(string json, string? expected)
    {
        HookPayloadReader.PeekEventName(Encoding.UTF8.GetBytes(json)).ShouldBe(expected);
    }
}
