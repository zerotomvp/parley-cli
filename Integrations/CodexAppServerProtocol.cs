using System.Text.Json.Serialization;

namespace ParleyCli.Integrations;

internal sealed class DaemonVersionStatus
{
    public string? Status { get; init; }
    public string? SocketPath { get; init; }
}

internal class RpcResponse
{
    public int? Id { get; init; }
    public RpcError? Error { get; init; }
}

internal sealed class RpcResponse<TResult> : RpcResponse
{
    public TResult? Result { get; init; }
}

internal sealed class RpcError
{
    public int? Code { get; init; }
    public string? Message { get; init; }
}

internal sealed class RpcRequest<TParams>
{
    public required string Method { get; init; }
    public int? Id { get; init; }

    [JsonPropertyName("params")]
    public required TParams Params { get; init; }
}

internal sealed class EmptyParams;

internal sealed class InitializeParams
{
    public required ClientInfo ClientInfo { get; init; }
}

internal sealed class ClientInfo
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Version { get; init; }
}

internal sealed class LoadedThreadsResult
{
    public string[] Data { get; init; } = [];
}

internal sealed class ThreadReadParams
{
    public required string ThreadId { get; init; }
    public bool IncludeTurns { get; init; }
}

internal sealed class ThreadReadResult
{
    public AppServerThread? Thread { get; init; }
}

internal sealed class AppServerThread
{
    public string? Id { get; init; }
    public string? Path { get; init; }
    public AppServerThreadStatus? Status { get; init; }
    public AppServerTurn[] Turns { get; init; } = [];
}

internal sealed class AppServerThreadStatus
{
    public string? Type { get; init; }
}

internal sealed class AppServerTurn
{
    public string? Id { get; init; }
    public string? Status { get; init; }
}

internal class TurnStartParams
{
    public required string ThreadId { get; init; }
    public string? ClientUserMessageId { get; init; }
    public required TextInput[] Input { get; init; }
}

internal sealed class TurnSteerParams : TurnStartParams
{
    public required string ExpectedTurnId { get; init; }
}

internal sealed class TextInput
{
    public string Type { get; init; } = "text";
    public required string Text { get; init; }
}

internal sealed class CodexRolloutEntry
{
    public string? Type { get; init; }
    public CodexRolloutPayload? Payload { get; init; }
}

internal sealed class CodexRolloutPayload
{
    public string? Type { get; init; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    [JsonPropertyName("turn_id")]
    public string? TurnId { get; init; }
}
