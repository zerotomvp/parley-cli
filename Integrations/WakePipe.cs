using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace ParleyCli.Integrations;

/// <summary>Shared same-user named-pipe transport for harness wake adapters.</summary>
internal static class WakePipe
{
    internal static string Name(string harness, string sid)
    {
        var home = Environment.GetEnvironmentVariable("PARLEY_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".parley");
        var identity = $"{Path.GetFullPath(home)}\n{sid}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"parley-{harness}-{hash[..24]}";
    }

    internal static async Task<WakeResult> SendAsync(
        string harness,
        string sid,
        string? notification,
        CancellationToken ct,
        TimeSpan timeout)
    {
        var operation = Guid.NewGuid().ToString("N")[..8];
        var pipeName = Name(harness, sid);
        var kind = notification is null ? "probe" : "wake";
        var elapsed = Stopwatch.StartNew();
        var connected = false;
        Log.Verbose("[trace] {Harness} pipe client {Operation} begin; kind={Kind} sid={Sid} pipe={Pipe} notificationLength={NotificationLength}",
            harness, operation, kind, sid, pipeName, notification?.Length ?? 0);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            Log.Verbose("[trace] {Harness} pipe client {Operation} connecting; timeoutMs={TimeoutMs}",
                harness, operation, timeout.TotalMilliseconds);
            await pipe.ConnectAsync(deadline.Token);
            connected = true;

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(notification ?? string.Empty);
            var response = await reader.ReadLineAsync(deadline.Token);
            Log.Verbose("[trace] {Harness} pipe client {Operation} completed after {ElapsedMs}ms; acknowledged={Acknowledged}",
                harness, operation, elapsed.ElapsedMilliseconds, response == "ok");
            return response == "ok"
                ? new(WakeStatus.Woken)
                : new(WakeStatus.Failed, response ?? $"{harness} channel closed without acknowledging the notice.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            Log.Verbose(ex,
                "[trace] {Harness} pipe client {Operation} failed after {ElapsedMs}ms; kind={Kind} connected={Connected} classification={Classification}",
                harness, operation, elapsed.ElapsedMilliseconds, kind, connected,
                connected ? "failed" : "unavailable");
            return connected ? new(WakeStatus.Failed, ex.Message) : new(WakeStatus.Unavailable);
        }
    }
}
