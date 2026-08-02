using Microsoft.Extensions.DependencyInjection;

namespace ParleyCli.Integrations;

/// <summary>
/// Constructs the single wake integration recorded for a recipient role.
/// </summary>
public sealed class WakeClientFactory(IServiceProvider services)
{
    public IWakeClient? Create(string wake) => wake switch
    {
        "claude" => services.GetRequiredService<ClaudeWakeClient>(),
        "codex" => services.GetRequiredService<CodexWakeClient>(),
        "never" => null,
        _ => throw new ArgumentException($"Unknown stored wake type '{wake}'.")
    };
}
