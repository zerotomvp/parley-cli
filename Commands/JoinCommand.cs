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

        var command = new Command("join", "Claim a role on a channel (required before send/recv). Role via --as.")
        {
            channelArg, forceOpt
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();
            var codexWake = Cli.Services.GetRequiredService<CodexWakeClient>();

            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var me = ResolveIdentity(pr);
            var force = pr.GetValue(forceOpt);

            var result = store.Join(channel, me.Role, me.Sid, force);
            var msg = result switch
            {
                ChannelStore.JoinResult.AlreadyYours => $"already holds role [blue]{Markup.Escape(me.Role)}[/]",
                ChannelStore.JoinResult.Reclaimed    => $"reclaimed role [blue]{Markup.Escape(me.Role)}[/] (was another session)",
                _                                    => $"joined [blue]{Markup.Escape(channel)}[/] as [blue]{Markup.Escape(me.Role)}[/]",
            };
            Stderr.MarkupLine($"[green]✓[/] {msg} [grey](sid {Markup.Escape(ShortSid(me.Sid))})[/]");
            var checkpoint = result == ChannelStore.JoinResult.Reclaimed
                ? store.GetCursor(channel, me.Sid)
                : 0;
            if (await codexWake.IsLoadedAsync(me.Sid, ct))
            {
                Stderr.MarkupLine("[green]✓[/] automatic Codex wake-up is available for this thread");
                Stderr.MarkupLine("[grey]Do not maintain a blocking recv listener. Incoming notifications will tell you to receive.[/]");
                Stderr.MarkupLine($"[grey]Recovery: parley recv {Markup.Escape(channel)} --as {Markup.Escape(me.Role)} --last-seen {checkpoint}[/]");
            }
            else
            {
                Stderr.MarkupLine("[yellow]Note:[/] automatic wake-up is not currently available; keep this listener running:");
                Stderr.MarkupLine($"[grey]parley recv {Markup.Escape(channel)} --as {Markup.Escape(me.Role)} --last-seen {checkpoint} --wait[/]");
            }
            return 0;
        }));

        return command;
    }
}
