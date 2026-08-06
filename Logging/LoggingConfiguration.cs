using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParleyCli.Logging;

public static class LoggingConfiguration
{
    public const string TraceEnvironmentVariable = "PARLEY_TRACE";
    public const string ConfigFileName = "config.json";

    private static readonly Lazy<TraceSetting> Trace = new(ResolveTraceSetting);

    public static bool TraceEnabled => Trace.Value.Enabled;
    public static string TraceSource => Trace.Value.Source;
    public static string? ConfigurationWarning => Trace.Value.Warning;

    public static string ConfigPath => Path.Combine(AppDataDirectory(), ConfigFileName);

    public static LogEventLevel InitialLevel =>
        TraceEnabled ? LogEventLevel.Verbose : LogEventLevel.Information;

    public static Logger CreateLogger(LoggingLevelSwitch levelSwitch)
    {
        var logDir = Path.Combine(AppDataDirectory(), "logs");
        Directory.CreateDirectory(logDir);

        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3} pid={ProcessId}] {Message:lj}{NewLine}{Exception}",
                theme: AnsiConsoleTheme.Code,
                standardErrorFromLevel: LogEventLevel.Verbose)
            .WriteTo.File(
                path: Path.Combine(logDir, "parley-cli-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} pid={ProcessId}] {Message:lj}{NewLine}{Exception}",
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

    private static TraceSetting ResolveTraceSetting()
    {
        var environmentValue = Environment.GetEnvironmentVariable(TraceEnvironmentVariable);
        if (environmentValue is not null)
            return new TraceSetting(IsEnabled(environmentValue), TraceEnvironmentVariable);

        if (!File.Exists(ConfigPath))
            return new TraceSetting(false, "default");

        try
        {
            using var stream = File.OpenRead(ConfigPath);
            var config = JsonSerializer.Deserialize<ParleyConfig>(stream);
            return new TraceSetting(config?.Trace == true, ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TraceSetting(false, ConfigPath,
                $"Could not read tracing configuration from {ConfigPath}: {ex.Message}");
        }
    }

    private static bool IsEnabled(string value) => value.Trim().ToLowerInvariant()
        is "1" or "true" or "yes" or "on";

    private static string AppDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "parley-cli");

    private sealed record TraceSetting(bool Enabled, string Source, string? Warning = null);

    private sealed class ParleyConfig
    {
        [JsonPropertyName("trace")]
        public bool? Trace { get; init; }
    }
}
