using System.Text.Json.Serialization;

namespace Engram.Tests;

/// <summary>Source-generated serialization for test payloads.</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class PayloadJson : JsonSerializerContext;
