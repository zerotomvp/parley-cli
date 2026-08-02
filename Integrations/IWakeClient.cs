namespace ParleyCli.Integrations;

public enum WakeStatus { Unavailable, Woken, Failed }

public readonly record struct WakeResult(WakeStatus Status, string? Error = null);

public interface IWakeClient
{
    string Name { get; }
    string TransportName { get; }
    Task<WakeResult> ProbeAsync(string sid, CancellationToken ct);
    Task<WakeResult> WakeAsync(string sid, string notification, CancellationToken ct);
}
