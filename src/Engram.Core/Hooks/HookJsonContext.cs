using System.Text.Json.Serialization;

namespace Engram.Core.Hooks;

/// <summary>
/// Source-generated (AOT-safe) serialization for hook payloads. Missing <c>required</c> members and
/// nulls in non-nullable members both throw, which is what the contract tests rely on.
/// </summary>
[JsonSourceGenerationOptions(
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(SessionStartPayload))]
[JsonSerializable(typeof(UserPromptSubmitPayload))]
[JsonSerializable(typeof(PreToolUsePayload))]
[JsonSerializable(typeof(PostToolUsePayload))]
[JsonSerializable(typeof(PostToolUseFailurePayload))]
[JsonSerializable(typeof(PostToolBatchPayload))]
[JsonSerializable(typeof(StopPayload))]
[JsonSerializable(typeof(SubagentStopPayload))]
[JsonSerializable(typeof(PreCompactPayload))]
[JsonSerializable(typeof(PostCompactPayload))]
[JsonSerializable(typeof(SessionEndPayload))]
[JsonSerializable(typeof(UnknownHookPayload))]
public sealed partial class HookJsonContext : JsonSerializerContext;
