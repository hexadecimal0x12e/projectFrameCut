using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Shared;

public static class RpcProtocol
{
    
    public sealed class RpcMessage
    {
        [JsonPropertyName("Type")]
        public required string Type { get; init; }

        [JsonPropertyName("Payload")]
        public JsonElement? Payload { get; init; }

        [JsonPropertyName("RequestId")]
        public ulong RequestId { get; init; }
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}