using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ParleyCli.Serialization;
using Serilog;

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
        var pipeName = ClaudeWakeClient.PipeName(sid);
        Log.Verbose("[trace] Claude channel server starting; sid={Sid} pipe={Pipe}", sid, pipeName);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pipe = RunPipeAsync(sid, stop.Token);
        var mcp = RunMcpAsync(stop.Token);
        var completed = await Task.WhenAny(mcp, pipe);
        Log.Verbose("[trace] Claude channel server component completed; component={Component} status={Status}",
            ReferenceEquals(completed, mcp) ? "mcp" : "pipe", completed.Status);
        try
        {
            await completed;
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "[trace] Claude channel server component faulted; component={Component}",
                ReferenceEquals(completed, mcp) ? "mcp" : "pipe");
            throw;
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(mcp, pipe); } catch (OperationCanceledException) { }
            Log.Verbose("[trace] Claude channel server stopped; sid={Sid}", sid);
        }
    }

    private async Task RunMcpAsync(CancellationToken ct)
    {
        while (await Console.In.ReadLineAsync(ct) is { } line)
        {
            Log.Verbose("[trace] Claude MCP frame received; length={FrameLength}", line.Length);
            ClaudeMcpRequest? request;
            try { request = JsonSerializer.Deserialize(line, ParleyJsonContext.Default.ClaudeMcpRequest); }
            catch (JsonException ex)
            {
                Log.Verbose(ex, "[trace] Claude MCP frame rejected as invalid JSON; length={FrameLength}", line.Length);
                continue;
            }
            if (request?.Method is null)
            {
                Log.Verbose("[trace] Claude MCP frame ignored because method is absent");
                continue;
            }
            Log.Verbose("[trace] Claude MCP request; method={Method} hasId={HasId}", request.Method, request.Id is not null);

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
                    Log.Verbose("[trace] Claude MCP initialized notification received");
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
        var pipeName = ClaudeWakeClient.PipeName(sid);
        var instance = 0L;
        while (!ct.IsCancellationRequested)
        {
            var current = Interlocked.Increment(ref instance);
            try
            {
                Log.Verbose("[trace] Claude pipe server creating instance {Instance}; sid={Sid} pipe={Pipe}", current, sid, pipeName);
                await using var pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                Log.Verbose("[trace] Claude pipe server instance {Instance} waiting for connection", current);
                await pipe.WaitForConnectionAsync(ct);
                Log.Verbose("[trace] Claude pipe server instance {Instance} connected", current);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                    { AutoFlush = true };
                var notification = await reader.ReadLineAsync(ct);
                var kind = string.IsNullOrEmpty(notification) ? "probe" : "wake";
                Log.Verbose("[trace] Claude pipe server instance {Instance} received {Kind}; notificationLength={NotificationLength} initialized={Initialized}",
                    current, kind, notification?.Length ?? 0, _initialized.Task.IsCompleted);
                var initializedWait = Stopwatch.StartNew();
                await _initialized.Task.WaitAsync(ct);
                Log.Verbose("[trace] Claude pipe server instance {Instance} initialization gate passed after {ElapsedMs}ms", current, initializedWait.ElapsedMilliseconds);
                if (!string.IsNullOrEmpty(notification))
                {
                    Log.Verbose("[trace] Claude pipe server instance {Instance} emitting channel notification", current);
                    await WriteAsync(new ClaudeChannelNotification(
                        "2.0", "notifications/claude/channel", new ClaudeChannelParams(notification)), ct);
                    Log.Verbose("[trace] Claude pipe server instance {Instance} emitted channel notification", current);
                }
                await writer.WriteLineAsync("ok");
                Log.Verbose("[trace] Claude pipe server instance {Instance} acknowledged {Kind}", current, kind);
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                Log.Verbose(ex, "[trace] Claude pipe server instance {Instance} ended with I/O failure", current);
            }
        }
        Log.Verbose("[trace] Claude pipe server loop cancelled; sid={Sid}", sid);
    }

    private async Task WriteAsync<T>(T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, typeof(T), ParleyJsonContext.Default);
        Log.Verbose("[trace] Claude MCP write queued; frameType={FrameType} length={FrameLength}", typeof(T).Name, json.Length);
        await _stdout.WaitAsync(ct);
        try
        {
            await Console.Out.WriteLineAsync(json);
            await Console.Out.FlushAsync(ct);
            Log.Verbose("[trace] Claude MCP write completed; frameType={FrameType}", typeof(T).Name);
        }
        finally { _stdout.Release(); }
    }

    private static string ServerVersion() =>
        typeof(ClaudeChannelServer).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
