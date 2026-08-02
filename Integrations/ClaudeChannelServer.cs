using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ParleyCli.Serialization;

namespace ParleyCli.Integrations;

/// <summary>A minimal one-way Claude Code MCP channel server over stdio.</summary>
public sealed class ClaudeChannelServer
{
    private const string Instructions =
        "Parley events are wake notices for durable agent-to-agent messages. " +
        "Run the exact parley recv command in each notice; the notice itself is not message delivery.";

    private readonly SemaphoreSlim _stdout = new(1, 1);
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(string sid, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pipe = RunPipeAsync(sid, stop.Token);
        var mcp = RunMcpAsync(stop.Token);
        var completed = await Task.WhenAny(mcp, pipe);
        await completed;
        stop.Cancel();
        try { await Task.WhenAll(mcp, pipe); } catch (OperationCanceledException) { }
    }

    private async Task RunMcpAsync(CancellationToken ct)
    {
        while (await Console.In.ReadLineAsync(ct) is { } line)
        {
            ClaudeMcpRequest? request;
            try { request = JsonSerializer.Deserialize(line, ParleyJsonContext.Default.ClaudeMcpRequest); }
            catch (JsonException) { continue; }
            if (request?.Method is null) continue;

            switch (request.Method)
            {
                case "initialize":
                    var version = request.Params?.ProtocolVersion ?? "2025-06-18";
                    await WriteAsync(new ClaudeMcpResponse<ClaudeInitializeResult>(
                        "2.0", request.Id,
                        new ClaudeInitializeResult(
                            version,
                            new ClaudeCapabilities(
                                new Dictionary<string, ClaudeEmptyCapability> { ["claude/channel"] = new() }),
                            new ClaudeServerInfo("parley", ServerVersion()),
                            Instructions)), ct);
                    break;
                case "notifications/initialized":
                    _initialized.TrySetResult();
                    break;
                case "ping":
                    await WriteAsync(new ClaudeMcpResponse<ClaudeEmptyResult>("2.0", request.Id, new()), ct);
                    break;
                case "tools/list":
                    await WriteAsync(new ClaudeMcpResponse<ClaudeToolsListResult>(
                        "2.0", request.Id, new ClaudeToolsListResult([])), ct);
                    break;
                default:
                    if (!request.Method.StartsWith("notifications/", StringComparison.Ordinal) && request.Id is not null)
                        await WriteAsync(new ClaudeMcpErrorResponse(
                            "2.0", request.Id, new ClaudeMcpError(-32601, $"method not found: {request.Method}")), ct);
                    break;
            }
        }
    }

    private async Task RunPipeAsync(string sid, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    ClaudeWakeClient.PipeName(sid), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                    { AutoFlush = true };
                var notification = await reader.ReadLineAsync(ct);
                await _initialized.Task.WaitAsync(ct);
                if (!string.IsNullOrEmpty(notification))
                {
                    await WriteAsync(new ClaudeChannelNotification(
                        "2.0", "notifications/claude/channel", new ClaudeChannelParams(notification)), ct);
                }
                await writer.WriteLineAsync("ok");
            }
            catch (IOException) when (!ct.IsCancellationRequested) { }
        }
    }

    private async Task WriteAsync<T>(T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, typeof(T), ParleyJsonContext.Default);
        await _stdout.WaitAsync(ct);
        try
        {
            await Console.Out.WriteLineAsync(json);
            await Console.Out.FlushAsync(ct);
        }
        finally { _stdout.Release(); }
    }

    private static string ServerVersion() =>
        typeof(ClaudeChannelServer).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
