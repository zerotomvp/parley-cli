using Microsoft.Extensions.DependencyInjection;

namespace ParleyCli.Integrations;

/// <summary>
/// Constructs the single wake integration recorded for a recipient role.
/// </summary>
public sealed class WakeClientFactory(IServiceProvider services)
{
    public IWakeClient? Create(string wake)
    {
        if (wake == "never") return null;
        var harness = HarnessCatalog.Find(wake)
            ?? throw new ArgumentException($"Unknown stored wake type '{wake}'.");
        return (IWakeClient)services.GetRequiredService(harness.WakeClientType);
    }
}
