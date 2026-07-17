using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace ParleyCli.Logging;

public static class LoggingConfiguration
{
    public static Logger CreateLogger(LoggingLevelSwitch levelSwitch)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "parley-cli", "logs");
        Directory.CreateDirectory(logDir);

        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                theme: AnsiConsoleTheme.Code,
                standardErrorFromLevel: LogEventLevel.Verbose)
            .WriteTo.File(
                path: Path.Combine(logDir, "parley-cli-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 30)
            .CreateLogger();
    }

    public static LogEventLevel ParseLevel(string logLevel) => logLevel.ToLowerInvariant() switch
    {
        "trace" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "info"  => LogEventLevel.Information,
        "warn"  => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        _       => LogEventLevel.Information
    };
}
