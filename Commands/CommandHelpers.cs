using System.CommandLine;
using System.Text;
using System.Text.Json;
using ParleyCli.Channels;
using ParleyCli.Logging;
using ParleyCli.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Spectre.Console;

namespace ParleyCli.Commands;

public static class CommandHelpers
{
    /// <summary>stderr-bound console — all status/error output goes here so stdout stays message data only.</summary>
    public static readonly IAnsiConsole Stderr = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(Console.Error)
    });

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Applies the --log-level global option to the active logging switch.</summary>
    public static void ApplyLogLevel(ParseResult pr)
    {
        var levelSwitch = Cli.Services.GetRequiredService<LoggingLevelSwitch>();
        levelSwitch.MinimumLevel = LoggingConfiguration.ParseLevel(pr.GetValue(GlobalOptions.LogLevel) ?? "info");
    }

    /// <summary>Resolves the participant id from --as or the PARLEY_ID env var; throws if neither is set.</summary>
    public static string ResolveId(ParseResult pr)
    {
        var id = pr.GetValue(GlobalOptions.As)
                 ?? Environment.GetEnvironmentVariable("PARLEY_ID");
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException(
                "No participant id. Pass --as <id> or set the PARLEY_ID env var (e.g. PARLEY_ID=claude).");
        return ChannelStore.Validate("participant id", id);
    }

    /// <summary>Writes messages to stdout — human-readable by default, one compact JSON object per line with --json.</summary>
    public static void PrintMessages(IReadOnlyList<Message> messages, bool json)
    {
        if (json)
        {
            foreach (var m in messages)
                Console.WriteLine(JsonSerializer.Serialize(m, JsonOpts));
            return;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (i > 0) sb.AppendLine();
            sb.AppendLine($"{m.From} · {FormatTime(m.Ts)} · #{m.Seq}");
            sb.AppendLine(m.Text);
        }
        Console.Write(sb.ToString());
    }

    private static string FormatTime(string iso) =>
        DateTimeOffset.TryParse(iso, out var dto)
            ? dto.ToLocalTime().ToString("HH:mm:ss")
            : iso;

    /// <summary>
    /// Wraps an action handler with top-level error handling. System.CommandLine v2 does
    /// not propagate action-thrown exceptions to the outer Program.cs catch, so we catch
    /// them here and surface a clean, actionable message instead of a stack trace.
    /// </summary>
    public static Func<ParseResult, CancellationToken, Task<int>> Safe(
        Func<ParseResult, CancellationToken, Task<int>> action) =>
        async (pr, ct) =>
        {
            try { return await action(pr, ct); }
            catch (OperationCanceledException)
            {
                return 130; // interrupted (Ctrl+C)
            }
            catch (ArgumentException ex)
            {
                Stderr.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
                return 1;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Unhandled error in command");
                Stderr.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
                return 1;
            }
        };
}
