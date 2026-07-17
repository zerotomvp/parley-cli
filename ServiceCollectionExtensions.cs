using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using Serilog.Core;

namespace ParleyCli;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddParleyServices(this IServiceCollection services, LoggingLevelSwitch levelSwitch)
    {
        services.AddSingleton(levelSwitch);
        services.AddSingleton<ChannelStore>();
        return services;
    }
}
