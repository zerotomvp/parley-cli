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
        WriteIndented = false,
        // Match the stored form: omit nulls only (see ChannelStore), so "closed" appears
        // only on closing messages without dropping meaningful defaults elsewhere.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Applies the --log-level global option to the active logging switch.</summary>
    public static void ApplyLogLevel(ParseResult pr)
    {
        var levelSwitch = Cli.Services.GetRequiredService<LoggingLevelSwitch>();
        levelSwitch.MinimumLevel = LoggingConfiguration.ParseLevel(pr.GetValue(GlobalOptions.LogLevel) ?? "info");
    }

    /// <summary>
    /// This session's identity on a channel: a unique <see cref="Sid"/> (session id — used to
    /// tag cursors and filter "not me", so any number of sessions can share a channel) and a
    /// human-readable <see cref="Label"/> shown as the message sender.
    /// </summary>
    public readonly record struct Identity(string Sid, string Label);

    /// <summary>
    /// Resolves this session's identity. Precedence: an explicit <c>--as</c>/<c>PARLEY_ID</c>
    /// (sets both sid and label, for manual/testing use) → the agent runtime's own session id,
    /// which persists across the agent's separate shells (<c>CODEX_THREAD_ID</c> → codex, checked
    /// first for the nested case; else <c>CLAUDE_CODE_SESSION_ID</c> → claude). Throws if none apply.
    /// </summary>
    public static Identity ResolveIdentity(ParseResult pr)
    {
        var explicitId = pr.GetValue(GlobalOptions.As)
                         ?? Environment.GetEnvironmentVariable("PARLEY_ID");
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            var v = ChannelStore.Validate("participant id", explicitId);
            return new Identity(v, v);
        }

        var codex = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        if (!string.IsNullOrWhiteSpace(codex))
            return new Identity(ChannelStore.Validate("session id", codex), "codex");

        var claude = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
        if (!string.IsNullOrWhiteSpace(claude))
            return new Identity(ChannelStore.Validate("session id", claude), "claude");

        throw new ArgumentException(
            "Could not determine session identity. Running under Claude Code or Codex sets it " +
            "automatically; otherwise pass --as <id> or set the PARLEY_ID env var.");
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
            sb.AppendLine($"{m.From} · {FormatTime(m.Ts)} · #{m.Seq}{(m.Closed == true ? " · [closed]" : "")}");
            sb.AppendLine(m.Text);
        }
        Console.Write(sb.ToString());
    }

    /// <summary>If any received message marks the exchange closed, tell the reader to stop waiting.</summary>
    public static void NoteIfClosed(IReadOnlyList<Message> messages)
    {
        if (messages.Any(m => m.Closed == true))
            Stderr.MarkupLine("[yellow]The other side marked the exchange closed[/] — no reply expected; do not wait for more.");
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
