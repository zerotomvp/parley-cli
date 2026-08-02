using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParleyCli.Integrations;

public sealed record ClaudeMcpRequest(
    string? Method,
    JsonElement? Id,
    [property: JsonPropertyName("params")] ClaudeMcpParams? Params);

public sealed record ClaudeMcpParams(string? ProtocolVersion);

public sealed record ClaudeMcpResponse<T>(string Jsonrpc, JsonElement? Id, T Result);

public sealed record ClaudeMcpErrorResponse(string Jsonrpc, JsonElement? Id, ClaudeMcpError Error);

public sealed record ClaudeMcpError(int Code, string Message);

public sealed record ClaudeInitializeResult(
    string ProtocolVersion,
    ClaudeCapabilities Capabilities,
    ClaudeServerInfo ServerInfo,
    string Instructions);

public sealed record ClaudeCapabilities(
    Dictionary<string, ClaudeEmptyCapability> Experimental);

public sealed record ClaudeEmptyCapability;

public sealed record ClaudeEmptyResult;

public sealed record ClaudeServerInfo(string Name, string Version);

public sealed record ClaudeToolsListResult(JsonElement[] Tools);

public sealed record ClaudeChannelNotification(
    string Jsonrpc,
    string Method,
    [property: JsonPropertyName("params")] ClaudeChannelParams Params);

public sealed record ClaudeChannelParams(string Content);
