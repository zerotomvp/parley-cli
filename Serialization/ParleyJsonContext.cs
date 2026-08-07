using System.Text.Json.Serialization;
using ParleyCli.Integrations;
using ParleyCli.Models;

namespace ParleyCli.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(MessageWire))]
[JsonSerializable(typeof(RosterEntryWire))]
[JsonSerializable(typeof(Participant))]
[JsonSerializable(typeof(SequenceOutput))]
[JsonSerializable(typeof(DaemonVersionStatus))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(RpcResponse<LoadedThreadsResult>))]
[JsonSerializable(typeof(RpcResponse<ThreadReadResult>))]
[JsonSerializable(typeof(RpcRequest<InitializeParams>))]
[JsonSerializable(typeof(RpcRequest<EmptyParams>))]
[JsonSerializable(typeof(RpcRequest<ThreadReadParams>))]
[JsonSerializable(typeof(RpcRequest<TurnStartParams>))]
[JsonSerializable(typeof(RpcRequest<TurnSteerParams>))]
[JsonSerializable(typeof(ClaudeMcpRequest))]
[JsonSerializable(typeof(ClaudeMcpResponse<ClaudeInitializeResult>))]
[JsonSerializable(typeof(ClaudeMcpResponse<ClaudeToolsListResult>))]
[JsonSerializable(typeof(ClaudeMcpResponse<ClaudeEmptyResult>))]
[JsonSerializable(typeof(ClaudeMcpErrorResponse))]
[JsonSerializable(typeof(ClaudeChannelNotification))]
[JsonSerializable(typeof(ClaudeAgentInfo[]))]
[JsonSerializable(typeof(ClaudeEndpointRegistration))]
[JsonSerializable(typeof(CodexRolloutEntry))]
internal partial class ParleyJsonContext : JsonSerializerContext;
