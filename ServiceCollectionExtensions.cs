using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using ParleyCli.Integrations;
using Serilog.Core;

namespace ParleyCli;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddParleyServices(this IServiceCollection services, LoggingLevelSwitch levelSwitch)
    {
        services.AddSingleton(levelSwitch);
        services.AddSingleton<ChannelStore>();
        services.AddSingleton<CodexWakeClient>();
        services.AddSingleton<ClaudeWakeClient>();
        services.AddSingleton<PiWakeClient>();
        services.AddSingleton<ClaudeAgentDiscovery>();
        services.AddSingleton<ClaudeEndpointRegistry>();
        services.AddSingleton<ClaudeSessionResolver>();
        services.AddSingleton<WakeClientFactory>();
        services.AddSingleton<ClaudeChannelServer>();
        services.AddSingleton<PiChannelServer>();
        return services;
    }
}
