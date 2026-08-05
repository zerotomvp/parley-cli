using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using ParleyCli.Integrations;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>
/// Claim a role on a channel. Binds the role to this session's id so no other session can send or
/// receive as it; a second session claiming the same role is rejected (use --force to take it over,
/// e.g. after a restart under a new session id). Required before send/recv.
/// </summary>
public static class JoinCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string>("channel")
        {
            Description = "Channel name. Add a random 5-letter suffix to avoid collisions, e.g. review-xyzab."
        };
        var forceOpt = new Option<bool>("--force")
        {
            Description = "Take over the role even if another session holds it (for reclaiming after a restart)"
        };
        var wakeOpt = new Option<string>("--wake")
        {
            Description = "Wake type: detect resolves the current harness; codex or claude selects it explicitly; never disables wake-up",
            DefaultValueFactory = _ => "detect"
        };

        var command = new Command("join", "Claim a role on a channel (required before send/recv). Role via --as.")
        {
            channelArg, forceOpt, wakeOpt
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();
            var wakeClients = Cli.Services.GetRequiredService<WakeClientFactory>();
            var claudeSessions = Cli.Services.GetRequiredService<ClaudeSessionResolver>();

            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var me = ResolveIdentity(pr);
            var force = pr.GetValue(forceOpt);
            var requestedWake = pr.GetValue(wakeOpt)!;
            var wake = ResolveWake(requestedWake);

            ClaudeProcessCorrelation? claudeProcess = null;
            if (wake == "claude")
            {
                await claudeSessions.TryRepairMembershipAsync(channel, me.Role, me.Sid, ct);
                claudeProcess = await claudeSessions.CaptureAsync(me.Sid, ct);
            }

            var result = store.Join(channel, me.Role, me.Sid, wake, force, claudeProcess);
            var msg = result switch
            {
                ChannelStore.JoinResult.AlreadyYours => $"already holds role [blue]{Markup.Escape(me.Role)}[/]",
                ChannelStore.JoinResult.Reclaimed    => $"reclaimed role [blue]{Markup.Escape(me.Role)}[/] (was another session)",
                _                                    => $"joined [blue]{Markup.Escape(channel)}[/] as [blue]{Markup.Escape(me.Role)}[/]",
            };
            Stderr.MarkupLine($"[green]✓[/] {msg} [grey](sid {Markup.Escape(ShortSid(me.Sid))})[/]");
            var resolution = requestedWake.Equals("detect", StringComparison.OrdinalIgnoreCase)
                ? "detected from the active harness"
                : "selected explicitly";
            Stderr.MarkupLine($"[green]✓[/] wake type [blue]{Markup.Escape(wake)}[/] persisted ({resolution})");
            var checkpoint = result == ChannelStore.JoinResult.Reclaimed
                ? store.GetCursor(channel, me.Sid)
                : 0;
            var wakeClient = wakeClients.Create(wake);
            if (wakeClient is not null)
            {
                var probe = await wakeClient.ProbeAsync(me.Sid, ct);
                if (probe.Status == WakeStatus.Woken)
                {
                    Stderr.MarkupLine($"[green]✓[/] live {Markup.Escape(wakeClient.TransportName)} endpoint is available for this {(wakeClient is CodexWakeClient ? "thread" : "session")}");
                    Stderr.MarkupLine("[grey]Do not maintain a blocking recv listener. Incoming notifications will tell you to receive.[/]");
                    Stderr.MarkupLine($"[grey]Recovery: parley recv {Markup.Escape(channel)} --as {Markup.Escape(me.Role)} --last-seen {checkpoint}[/]");
                }
                else
                {
                    var detail = probe.Status == WakeStatus.Failed && !string.IsNullOrWhiteSpace(probe.Error)
                        ? $" ({Markup.Escape(probe.Error)})"
                        : "";
                    Stderr.MarkupLine($"[yellow]Note:[/] wake type is configured, but its live endpoint is unavailable{detail}; keep this foreground listener running:");
                    Stderr.MarkupLine($"[grey]parley recv {Markup.Escape(channel)} --as {Markup.Escape(me.Role)} --last-seen {checkpoint} --wait[/]");
                    Stderr.MarkupLine("[yellow]Do not move the fallback listener into the background; its output must reach model context.[/]");
                }
            }
            else
            {
                Stderr.MarkupLine("[yellow]Note:[/] wake type never disables automatic wake-up; keep this foreground listener running:");
                Stderr.MarkupLine($"[grey]parley recv {Markup.Escape(channel)} --as {Markup.Escape(me.Role)} --last-seen {checkpoint} --wait[/]");
                Stderr.MarkupLine("[yellow]Do not move the fallback listener into the background; its output must reach model context.[/]");
            }
            return 0;
        }));

        return command;
    }

    private static string ResolveWake(string requested)
    {
        requested = requested.ToLowerInvariant();
        if (requested is "codex" or "claude" or "never") return requested;
        if (requested != "detect")
            throw new ArgumentException("--wake must be detect, codex, claude, or never.");
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_THREAD_ID"))) return "codex";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID"))) return "claude";
        throw new ArgumentException(
            "--wake detect could not identify Codex or Claude Code. Pass --wake never for a manual session.");
    }
}
