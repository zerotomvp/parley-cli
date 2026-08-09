using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using ParleyCli.Integrations;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands.Members;

/// <summary>Remove an active participant as the channel owner.</summary>
public static class RemoveMemberCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string>("channel") { Description = "Channel name" };
        var roleArg = new Argument<string>("role") { Description = "Active role to remove" };
        var command = new Command("remove", "Remove an active member (channel owner only).")
        {
            channelArg, roleArg
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();
            var claudeSessions = Cli.Services.GetRequiredService<ClaudeSessionResolver>();
            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var targetRole = ChannelStore.Validate("role", pr.GetValue(roleArg)!);
            var me = ResolveIdentity(pr);

            await claudeSessions.TryRepairMembershipAsync(channel, me.Role, me.Sid, ct);
            store.RemoveMember(channel, me.Role, me.Sid, targetRole);
            Stderr.MarkupLine(
                $"[green]✓[/] removed role [blue]{Markup.Escape(targetRole)}[/] from [blue]{Markup.Escape(channel)}[/]");
            return 0;
        }));

        return command;
    }
}
