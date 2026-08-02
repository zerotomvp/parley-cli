using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

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

    private async Task<WakeResult> SendAsync(string sid, string? notification, CancellationToken ct)
    {
        var connected = false;
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", PipeName(sid), PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            await pipe.ConnectAsync(timeout.Token);
            connected = true;

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(notification ?? string.Empty);
            var response = await reader.ReadLineAsync(timeout.Token);
            return response == "ok"
                ? new(WakeStatus.Woken)
                : new(WakeStatus.Failed, response ?? "Claude channel closed without acknowledging the notice.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return connected ? new(WakeStatus.Failed, ex.Message) : new(WakeStatus.Unavailable);
        }
    }

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
