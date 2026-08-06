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
    public const string ConfigEnvironmentVariable = "PARLEY_CONFIG";
    public const string ConfigFileName = "config.json";

    private static readonly Lazy<Settings> Current = new(ResolveSettings);

    public static bool TraceEnabled => Current.Value.TraceEnabled;
    public static string TraceSource => Current.Value.TraceSource;
    public static bool UpdateChecksEnabled => Current.Value.UpdateChecksEnabled;
    public static string? ConfigurationWarning => Current.Value.Warning;

    public static string ConfigPath =>
        Environment.GetEnvironmentVariable(ConfigEnvironmentVariable) is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(ApplicationDirectory, ConfigFileName);
    public static string ApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "parley-cli");

    public static LogEventLevel InitialLevel =>
        TraceEnabled ? LogEventLevel.Verbose : LogEventLevel.Information;

    public static Logger CreateLogger(LoggingLevelSwitch levelSwitch)
    {
        var logDir = Path.Combine(ApplicationDirectory, "logs");
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

    private static Settings ResolveSettings()
    {
        var environmentValue = Environment.GetEnvironmentVariable(TraceEnvironmentVariable);
        if (!File.Exists(ConfigPath))
            return new Settings(
                environmentValue is not null && IsEnabled(environmentValue),
                environmentValue is null ? "default" : TraceEnvironmentVariable,
                UpdateChecksEnabled: true);

        try
        {
            using var stream = File.OpenRead(ConfigPath);
            var config = JsonSerializer.Deserialize<ParleyConfig>(stream);
            return new Settings(
                environmentValue is null ? config?.Trace == true : IsEnabled(environmentValue),
                environmentValue is null ? ConfigPath : TraceEnvironmentVariable,
                config?.Updates?.Check ?? true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Settings(
                environmentValue is not null && IsEnabled(environmentValue),
                environmentValue is null ? ConfigPath : TraceEnvironmentVariable,
                UpdateChecksEnabled: true,
                $"Could not read Parley configuration from {ConfigPath}: {ex.Message}");
        }
    }

    private static bool IsEnabled(string value) => value.Trim().ToLowerInvariant()
        is "1" or "true" or "yes" or "on";

    private sealed record Settings(
        bool TraceEnabled,
        string TraceSource,
        bool UpdateChecksEnabled,
        string? Warning = null);

    private sealed class ParleyConfig
    {
        [JsonPropertyName("trace")]
        public bool? Trace { get; init; }

        [JsonPropertyName("updates")]
        public UpdateConfig? Updates { get; init; }
    }

    private sealed class UpdateConfig
    {
        [JsonPropertyName("check")]
        public bool? Check { get; init; }
    }
}
