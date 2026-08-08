using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ParleyCli.Serialization;
using Serilog;

namespace ParleyCli.Integrations;

/// <summary>
/// Bridges session-scoped named-pipe wake requests to a Pi extension over JSONL stdio.
/// </summary>
public sealed class PiChannelServer
{
    private readonly SemaphoreSlim _stdout = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PiChannelResponse>> _pending =
        new(StringComparer.Ordinal);
    private long _nextId;

    public async Task RunAsync(string sid, CancellationToken ct)
    {
        var pipeName = PiWakeClient.PipeName(sid);
        Log.Verbose("[trace] Pi channel server starting; sid={Sid} pipe={Pipe}", sid, pipeName);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pipe = RunPipeAsync(sid, stop.Token);
        await WriteAsync(new PiChannelEvent("ready", Sid: sid, Version: ServerVersion()), stop.Token);
        var responses = RunResponsesAsync(stop.Token);
        var completed = await Task.WhenAny(responses, pipe);

        try
        {
            await completed;
        }
        finally
        {
            stop.Cancel();
            foreach (var acknowledgement in _pending.Values)
                acknowledgement.TrySetCanceled(stop.Token);
            try { await Task.WhenAll(responses, pipe); }
            catch (OperationCanceledException) { }
            Log.Verbose("[trace] Pi channel server stopped; sid={Sid}", sid);
        }
    }

    private async Task RunResponsesAsync(CancellationToken ct)
    {
        while (await Console.In.ReadLineAsync(ct) is { } line)
        {
            PiChannelResponse? response;
            try { response = JsonSerializer.Deserialize(line, ParleyJsonContext.Default.PiChannelResponse); }
            catch (JsonException ex)
            {
                Log.Verbose(ex, "[trace] Pi channel response rejected as invalid JSON; length={FrameLength}", line.Length);
                continue;
            }

            if (response is null || string.IsNullOrWhiteSpace(response.Id))
            {
                Log.Verbose("[trace] Pi channel response ignored because id is absent");
                continue;
            }

            if (_pending.TryRemove(response.Id, out var acknowledgement))
                acknowledgement.TrySetResult(response);
            else
                Log.Verbose("[trace] Pi channel response ignored because id is not pending; id={Id}", response.Id);
        }
    }

    private async Task RunPipeAsync(string sid, CancellationToken ct)
    {
        var pipeName = PiWakeClient.PipeName(sid);
        var instance = 0L;
        while (!ct.IsCancellationRequested)
        {
            var current = Interlocked.Increment(ref instance);
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                Log.Verbose("[trace] Pi pipe server instance {Instance} waiting; sid={Sid} pipe={Pipe}",
                    current, sid, pipeName);
                await pipe.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                    { AutoFlush = true };
                var notification = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(notification))
                {
                    await writer.WriteLineAsync("ok");
                    Log.Verbose("[trace] Pi pipe server acknowledged probe; instance={Instance}", current);
                    continue;
                }

                var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var acknowledgement = new TaskCompletionSource<PiChannelResponse>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_pending.TryAdd(id, acknowledgement))
                    throw new InvalidOperationException($"Duplicate Pi wake id '{id}'.");

                try
                {
                    await WriteAsync(new PiChannelEvent("wake", Id: id, Notification: notification), ct);
                    var response = await acknowledgement.Task.WaitAsync(ct);
                    await writer.WriteLineAsync(response.Success
                        ? "ok"
                        : $"error:{response.Error ?? "Pi rejected the wake notice."}");
                    Log.Verbose("[trace] Pi pipe server received extension acknowledgement; id={Id} success={Success}",
                        id, response.Success);
                }
                finally
                {
                    _pending.TryRemove(id, out _);
                }
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                Log.Verbose(ex, "[trace] Pi pipe server instance {Instance} ended with I/O failure", current);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                Log.Error(ex, "Pi pipe server instance {Instance} failed unexpectedly; another accept will be attempted", current);
                await Task.Delay(100, ct);
            }
        }
    }

    private async Task WriteAsync(PiChannelEvent value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, ParleyJsonContext.Default.PiChannelEvent);
        await _stdout.WaitAsync(ct);
        try
        {
            await Console.Out.WriteLineAsync(json);
            await Console.Out.FlushAsync(ct);
        }
        finally { _stdout.Release(); }
    }

    private static string ServerVersion() =>
        ParleyVersion.Display;
}
