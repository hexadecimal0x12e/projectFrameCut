using System.Text.Json;

namespace projectFrameCut.McpCore;

public sealed record McpToolDefinition(string Name, string Description, object InputSchema);

public sealed record McpResponse(string JsonRpc, object? Result, object? Error, System.Text.Json.JsonElement? Id);

public sealed record McpRequest(string JsonRpc, string Method, JsonElement? Params, JsonElement? Id);

