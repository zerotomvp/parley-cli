namespace ParleyCli.Integrations;

/// <summary>Best-effort wake delivery to a live Parley Claude channel subprocess.</summary>
public sealed class ClaudeWakeClient : IWakeClient
{
    public string Name => "Claude Code";
    public string TransportName => "Claude Code channel";

    public Task<WakeResult> ProbeAsync(string sid, CancellationToken ct) =>
        WakePipe.SendAsync("claude", sid, null, ct, TimeSpan.FromMilliseconds(500));

    public async Task<WakeResult> WakeAsync(string sid, string notification, CancellationToken ct) =>
        await WakePipe.SendAsync("claude", sid, notification, ct, TimeSpan.FromSeconds(2));

    public Task<WakeResult> RebindAsync(string oldSid, string newSid, CancellationToken ct) =>
        WakePipe.SendAsync("claude", oldSid, RebindPrefix + newSid, ct, TimeSpan.FromSeconds(2));

    internal const string RebindPrefix = "@parley/rebind:";

    internal static string PipeName(string sid) => WakePipe.Name("claude", sid);
}
