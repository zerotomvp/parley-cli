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

    /// <summary>Channel used when none is given on the command line.</summary>
    public const string DefaultChannel = "default";

    /// <summary>Resolves and validates the channel from an optional positional (falls back to the default).</summary>
    public static string ResolveChannel(string? value) =>
        ChannelStore.Validate("channel", string.IsNullOrWhiteSpace(value) ? DefaultChannel : value);

    /// <summary>Applies the --log-level global option to the active logging switch.</summary>
    public static void ApplyLogLevel(ParseResult pr)
    {
        var levelSwitch = Cli.Services.GetRequiredService<LoggingLevelSwitch>();
        levelSwitch.MinimumLevel = LoggingConfiguration.ParseLevel(pr.GetValue(GlobalOptions.LogLevel) ?? "info");
    }

    /// <summary>
    /// Resolves this session's participant id. Precedence: <c>--as</c> → <c>PARLEY_ID</c> env →
    /// auto-detect from the runtime's own session marker (which persists across the agent's
    /// separate shell invocations). Throws if none apply.
    /// </summary>
    public static string ResolveId(ParseResult pr)
    {
        var explicitId = pr.GetValue(GlobalOptions.As)
                         ?? Environment.GetEnvironmentVariable("PARLEY_ID");
        if (!string.IsNullOrWhiteSpace(explicitId))
            return ChannelStore.Validate("participant id", explicitId);

        var detected = DetectRuntimeId();
        if (detected != null)
            return detected;

        throw new ArgumentException(
            "Could not determine participant id. Running under Claude Code or Codex sets it " +
            "automatically; otherwise pass --as <id> or set the PARLEY_ID env var.");
    }

    /// <summary>
    /// Infers the participant from the agent runtime's injected session env var. Codex is
    /// checked first: a Codex session nested inside Claude Code is the active driver, so its
    /// marker should win when both are present.
    /// </summary>
    private static string? DetectRuntimeId()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_THREAD_ID")))
            return "codex";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID")))
            return "claude";
        return null;
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
