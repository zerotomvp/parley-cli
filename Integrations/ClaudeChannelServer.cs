using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ParleyCli.Serialization;
using Serilog;
using System.Collections.Concurrent;
using ParleyCli.Channels;

namespace ParleyCli.Integrations;

/// <summary>A minimal one-way Claude Code MCP channel server over stdio.</summary>
public sealed class ClaudeChannelServer(ClaudeEndpointRegistry endpointRegistry)
{
    private const string Instructions =
        "Parley events are wake notices for durable agent-to-agent messages. " +
        "Run the exact nonblocking parley recv command in each notice without adding --wait or " +
        "moving it into the background; the notice itself is not message delivery.";

    private readonly SemaphoreSlim _stdout = new(1, 1);
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, Task> _pipeAliases = new(StringComparer.Ordinal);

    public async Task RunAsync(string sid, CancellationToken ct)
    {
        var pipeName = ClaudeWakeClient.PipeName(sid);
        Log.Verbose("[trace] Claude channel server starting; sid={Sid} pipe={Pipe}", sid, pipeName);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pipe = RunPipeAsync(sid, stop.Token);
        var registration = endpointRegistry.RegisterAsync(sid, stop.Token);
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
            ClaudeEndpointRegistrationHandle? registrationHandle = null;
            try { registrationHandle = await registration; }
            catch (OperationCanceledException) { }
            try { await Task.WhenAll(_pipeAliases.Values.Append(mcp).Append(pipe)); }
            catch (OperationCanceledException) { }
            registrationHandle?.Dispose();
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
                if (notification?.StartsWith(ClaudeWakeClient.RebindPrefix, StringComparison.Ordinal) == true)
                {
                    var newSid = ChannelStore.Validate("session id",
                        notification[ClaudeWakeClient.RebindPrefix.Length..]);
                    await EnsurePipeAliasAsync(newSid, ct);
                    await writer.WriteLineAsync("ok");
                    Log.Verbose("[trace] Claude pipe endpoint rebound; oldSid={OldSid} newSid={NewSid}", sid, newSid);
                    continue;
                }
                var kind = string.IsNullOrEmpty(notification) ? "probe" : "wake";
                Log.Verbose("[trace] Claude pipe server instance {Instance} received {Kind}; notificationLength={NotificationLength} initialized={Initialized}",
                    current, kind, notification?.Length ?? 0, _initialized.Task.IsCompleted);
                if (!string.IsNullOrEmpty(notification))
                {
                    var initializedWait = Stopwatch.StartNew();
                    await _initialized.Task.WaitAsync(ct);
                    Log.Verbose("[trace] Claude pipe server instance {Instance} initialization gate passed after {ElapsedMs}ms", current, initializedWait.ElapsedMilliseconds);
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
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                Log.Error(ex,
                    "Claude pipe server instance {Instance} failed unexpectedly; another accept will be attempted",
                    current);
                await Task.Delay(100, ct);
            }
        }
        Log.Verbose("[trace] Claude pipe server loop cancelled; sid={Sid}", sid);
    }

    private async Task EnsurePipeAliasAsync(string sid, CancellationToken ct)
    {
        if (_pipeAliases.ContainsKey(sid)) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = RunPipeAliasAsync(sid, ready, ct);
        if (!_pipeAliases.TryAdd(sid, task)) return;
        await ready.Task.WaitAsync(ct);
    }

    private async Task RunPipeAliasAsync(string sid, TaskCompletionSource ready, CancellationToken ct)
    {
        // Signal only once the loop has begun. A connecting client can safely race creation: the
        // named-pipe client waits until the first server instance is listening.
        ready.TrySetResult();
        await RunPipeAsync(sid, ct);
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
        ParleyVersion.Display;
}
