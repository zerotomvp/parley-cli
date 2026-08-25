using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using ParleyCli.Integrations;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>Vacate the role currently owned by this session.</summary>
public static class LeaveCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string>("channel") { Description = "Channel name" };
        var command = new Command("leave", "Leave a channel and vacate this session's role.")
        {
            channelArg
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();
            var wakeClients = Cli.Services.GetRequiredService<WakeClientFactory>();
            var claudeSessions = Cli.Services.GetRequiredService<ClaudeSessionResolver>();
            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var me = ResolveIdentity(pr);

            await claudeSessions.TryRepairMembershipAsync(channel, me.Role, me.Sid, ct);
            var notice = store.Leave(channel, me.Role, me.Sid);
            Stderr.MarkupLine(
                $"[green]✓[/] left [blue]{Markup.Escape(channel)}[/], vacated role [blue]{Markup.Escape(me.Role)}[/], and broadcast [blue]#{notice.Seq}[/]");

            foreach (var membership in store.Participants(channel))
            {
                var wakeClient = wakeClients.Create(membership.Wake);
                if (wakeClient is null) continue;

                var notification = WakeNotification.Create(notice.Seq, channel, membership.Role);
                var result = await wakeClient.WakeAsync(membership.Sid, notification, ct);
                if (result.Status == WakeStatus.Woken)
                    Stderr.MarkupLine($"[green]✓[/] woke [blue]{Markup.Escape(membership.Role)}[/] through {Markup.Escape(wakeClient.TransportName)}");
                else if (result.Status == WakeStatus.Unavailable)
                    Stderr.MarkupLine($"[yellow]Note:[/] leave notice remains delivered, but the live {Markup.Escape(wakeClient.TransportName)} endpoint for [blue]{Markup.Escape(membership.Role)}[/] is unavailable. Ask it to run: [blue]parley recv {Markup.Escape(channel)} --as {Markup.Escape(membership.Role)} --last-seen <seq>[/]");
                else if (result.Status == WakeStatus.Failed)
                    Stderr.MarkupLine($"[yellow]Note:[/] leave notice remains delivered, but waking [blue]{Markup.Escape(membership.Role)}[/] failed: {Markup.Escape(result.Error ?? "unknown error")}");
            }
            return 0;
        }));

        return command;
    }
}
