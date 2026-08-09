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
            var claudeSessions = Cli.Services.GetRequiredService<ClaudeSessionResolver>();
            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var me = ResolveIdentity(pr);

            await claudeSessions.TryRepairMembershipAsync(channel, me.Role, me.Sid, ct);
            store.Leave(channel, me.Role, me.Sid);
            Stderr.MarkupLine(
                $"[green]✓[/] left [blue]{Markup.Escape(channel)}[/] and vacated role [blue]{Markup.Escape(me.Role)}[/]");
            return 0;
        }));

        return command;
    }
}
