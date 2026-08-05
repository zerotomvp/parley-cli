using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Serilog;

namespace ParleyCli.Integrations;

/// <summary>Best-effort wake delivery to a live Parley Claude channel subprocess.</summary>
public sealed class ClaudeWakeClient : IWakeClient
{
    public string Name => "Claude Code";
    public string TransportName => "Claude Code channel";

    public Task<WakeResult> ProbeAsync(string sid, CancellationToken ct) =>
        SendAsync(sid, null, ct);

    public async Task<WakeResult> WakeAsync(string sid, string notification, CancellationToken ct) =>
        await SendAsync(sid, notification, ct);

    public Task<WakeResult> RebindAsync(string oldSid, string newSid, CancellationToken ct) =>
        SendAsync(oldSid, RebindPrefix + newSid, ct, TimeSpan.FromSeconds(2));

    private async Task<WakeResult> SendAsync(string sid, string? notification, CancellationToken ct,
        TimeSpan? operationTimeout = null)
    {
        var operation = Guid.NewGuid().ToString("N")[..8];
        var pipeName = PipeName(sid);
        var kind = notification is null ? "probe" : "wake";
        var elapsed = Stopwatch.StartNew();
        var connected = false;
        Log.Verbose("[trace] Claude pipe client {Operation} begin; kind={Kind} sid={Sid} pipe={Pipe} notificationLength={NotificationLength}",
            operation, kind, sid, pipeName, notification?.Length ?? 0);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutValue = operationTimeout ?? TimeSpan.FromMilliseconds(500);
            timeout.CancelAfter(timeoutValue);
            Log.Verbose("[trace] Claude pipe client {Operation} connecting; timeoutMs={TimeoutMs}",
                operation, timeoutValue.TotalMilliseconds);
            await pipe.ConnectAsync(timeout.Token);
            connected = true;
            Log.Verbose("[trace] Claude pipe client {Operation} connected after {ElapsedMs}ms", operation, elapsed.ElapsedMilliseconds);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            Log.Verbose("[trace] Claude pipe client {Operation} writing {Kind} frame", operation, kind);
            await writer.WriteLineAsync(notification ?? string.Empty);
            Log.Verbose("[trace] Claude pipe client {Operation} awaiting acknowledgement", operation);
            var response = await reader.ReadLineAsync(timeout.Token);
            Log.Verbose("[trace] Claude pipe client {Operation} completed after {ElapsedMs}ms; acknowledged={Acknowledged} responseLength={ResponseLength}",
                operation, elapsed.ElapsedMilliseconds, response == "ok", response?.Length ?? 0);
            return response == "ok"
                ? new(WakeStatus.Woken)
                : new(WakeStatus.Failed, response ?? "Claude channel closed without acknowledging the notice.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            Log.Verbose(ex,
                "[trace] Claude pipe client {Operation} failed after {ElapsedMs}ms; kind={Kind} connected={Connected} classification={Classification}",
                operation, elapsed.ElapsedMilliseconds, kind, connected, connected ? "failed" : "unavailable");
            return connected ? new(WakeStatus.Failed, ex.Message) : new(WakeStatus.Unavailable);
        }
    }

    internal const string RebindPrefix = "@parley/rebind:";

    internal static string PipeName(string sid)
    {
        var home = Environment.GetEnvironmentVariable("PARLEY_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".parley");
        var identity = $"{Path.GetFullPath(home)}\n{sid}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"parley-claude-{hash[..24]}";
    }
}
