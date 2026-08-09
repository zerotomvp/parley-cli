using System.CommandLine;
using System.Text;
using System.Text.Json;
using ParleyCli.Channels;
using ParleyCli.Integrations;
using ParleyCli.Logging;
using ParleyCli.Models;
using ParleyCli.Serialization;
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
        levelSwitch.MinimumLevel = LoggingConfiguration.TraceEnabled
            ? Serilog.Events.LogEventLevel.Verbose
            : LoggingConfiguration.ParseLevel(pr.GetValue(GlobalOptions.LogLevel) ?? "info");
    }

    /// <summary>
    /// This session's identity on a channel: its <see cref="Role"/> (the addressable name it
    /// claimed via <c>join</c>, shown as the message sender and used for addressing) and its unique
    /// <see cref="Sid"/> (session id — the role-ownership token and cursor key).
    /// </summary>
    public readonly record struct Identity(string Sid, string Role);

    /// <summary>
    /// Resolves this session's <c>sid</c> (session id). Precedence: explicit <c>--sid</c> /
    /// <c>PARLEY_ID</c> → the active runtime entry in <see cref="HarnessCatalog"/>. Returns null
    /// if none apply (manual use with no override) — callers may fall back to the role.
    /// </summary>
    public static string? ResolveSid(ParseResult pr)
    {
        var sid = pr.GetValue(GlobalOptions.Sid)
                  ?? Environment.GetEnvironmentVariable("PARLEY_ID")
                  ?? HarnessCatalog.ResolveSessionId();
        return string.IsNullOrWhiteSpace(sid) ? null : ChannelStore.Validate("session id", sid);
    }

    /// <summary>
    /// Resolves this session's identity: <c>--as &lt;role&gt;</c> is required (no auto-detected
    /// role — distinct sessions must pick distinct roles). The sid is auto-detected (see
    /// <see cref="ResolveSid"/>), falling back to the role itself when nothing injects one.
    /// </summary>
    public static Identity ResolveIdentity(ParseResult pr)
    {
        var role = pr.GetValue(GlobalOptions.As);
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException(
                "This session's role is required: pass --as <role> (the name you joined the channel as).");
        role = ChannelStore.Validate("role", role);

        // sid auto-detected; when no runtime/override supplies one, the role doubles as the sid
        // (single manual session owning its own role — harmless since role↔sid is then 1:1).
        var sid = ResolveSid(pr) ?? role;
        return new Identity(sid, role);
    }

    /// <summary>
    /// Writes messages to stdout — human-readable by default, one compact JSON object per line with
    /// --json. When <paramref name="previewChars"/> is set (log preview mode), each body is truncated
    /// to a head with a clear cut-off marker pointing at
    /// <c>parley messages show &lt;channel&gt; &lt;seq&gt;</c>;
    /// JSON output is never truncated (it carries the full body regardless).
    /// </summary>
    public static void PrintMessages(IReadOnlyList<Message> messages, bool json,
        int? previewChars = null, string? channel = null)
    {
        if (json)
        {
            foreach (var m in messages)
                Console.WriteLine(JsonSerializer.Serialize(m, ParleyJsonContext.Default.Message));
            return;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (i > 0) sb.AppendLine();
            var to = m.Broadcast == true ? "→ all"
                   : m.To is { Count: > 0 } ? "→ " + string.Join(",", m.To)
                   : "";
            var meta = string.Join(" · ", new[] { m.From, FormatTime(m.Ts), $"#{m.Seq}", to, m.Closed == true ? "[closed]" : "" }
                .Where(s => !string.IsNullOrEmpty(s)));
            sb.AppendLine(meta);

            if (previewChars is int max)
            {
                var (head, truncated) = Preview(m.Text, max);
                sb.AppendLine(head);
                if (truncated)
                    sb.AppendLine($"  … [truncated — full message: parley messages show {channel} {m.Seq}]");
            }
            else
            {
                sb.AppendLine(m.Text);
            }
        }
        Console.Write(sb.ToString());
    }

    /// <summary>
    /// Head of a message body for log preview: the first line, cut to <paramref name="max"/> chars.
    /// Returns whether anything was dropped (extra lines or an over-length first line) so the caller
    /// can mark the message as cut off early.
    /// </summary>
    public static (string head, bool truncated) Preview(string text, int max)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var firstBreak = normalized.IndexOf('\n');
        var truncated = firstBreak >= 0; // more lines follow the first
        var head = firstBreak >= 0 ? normalized[..firstBreak] : normalized;
        if (head.Length > max)
        {
            head = head[..max].TrimEnd();
            truncated = true;
        }
        return (head, truncated);
    }

    /// <summary>If any received message marks the exchange closed, tell the reader to stop waiting.</summary>
    public static void NoteIfClosed(IReadOnlyList<Message> messages)
    {
        if (messages.Any(m => m.Closed == true))
            Stderr.MarkupLine("[yellow]The other side marked the exchange closed[/] — no reply expected; do not wait for more.");
    }

    /// <summary>Short, display-friendly form of a (possibly long UUID/thread) session id.</summary>
    public static string ShortSid(string sid) => sid.Length <= 12 ? sid : sid[..12] + "…";

    public static string FormatTime(string iso) =>
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
