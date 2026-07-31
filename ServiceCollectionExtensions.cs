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
        return services;
    }
}
