using Microsoft.Extensions.DependencyInjection;

namespace ParleyCli;

public static class Cli
{
    private static IServiceProvider? _serviceProvider;

    public static IServiceProvider Services =>
        _serviceProvider ?? throw new InvalidOperationException(
            "Services not configured. Call Cli.ConfigureServices first.");

    public static void ConfigureServices(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        _serviceProvider = services.BuildServiceProvider();
    }
}
