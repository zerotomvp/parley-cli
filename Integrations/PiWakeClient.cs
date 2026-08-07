namespace ParleyCli.Integrations;

/// <summary>Best-effort wake delivery to the extension running in a live Pi session.</summary>
public sealed class PiWakeClient : IWakeClient
{
    public string Name => "Pi";
    public string TransportName => "Pi extension";

    public Task<WakeResult> ProbeAsync(string sid, CancellationToken ct) =>
        WakePipe.SendAsync("pi", sid, null, ct, TimeSpan.FromMilliseconds(500));

    public Task<WakeResult> WakeAsync(string sid, string notification, CancellationToken ct) =>
        WakePipe.SendAsync("pi", sid, notification, ct, TimeSpan.FromSeconds(2));

    internal static string PipeName(string sid) => WakePipe.Name("pi", sid);
}
