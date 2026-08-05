using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using ParleyCli.Integrations;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>
/// Remove the last message from a channel and roll back any cursor that had read it — retract a
/// mis-send. Owner-gated: by default you may only drop a message you sent (its sid must be yours,
/// so pass --as &lt;your-role&gt;). A human operator can pass --force to drop anyone's last message.
/// Confirms unless --yes; refuses non-interactively without --yes.
/// </summary>
public static class DropCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string>("channel") { Description = "Channel name" };
        var forceOpt = new Option<bool>("--force")
        {
            Description = "Operator override: drop the last message even if it isn't yours"
        };
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip the confirmation prompt" };

        var command = new Command("drop",
            "Remove your last message from a channel (retract a mis-send). Rolls back cursors that had read it.")
        {
            channelArg, forceOpt, yesOpt
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();
            var claudeSessions = Cli.Services.GetRequiredService<ClaudeSessionResolver>();

            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var force = pr.GetValue(forceOpt);
            var yes = pr.GetValue(yesOpt);

            var all = store.ReadAll(channel);
            if (all.Count == 0)
            {
                Stderr.MarkupLine($"[grey]Channel '{Markup.Escape(channel)}' has no messages to drop.[/]");
                return 0;
            }

            var last = all[^1];

            // Owner gate: the last message must be mine, unless the operator forces it.
            if (!force)
            {
                var me = ResolveIdentity(pr); // requires --as
                await claudeSessions.TryRepairMembershipAsync(channel, me.Role, me.Sid, ct);
                if (!store.IsSameSession(channel, last.Sid, me.Sid))
                {
                    Stderr.MarkupLine(
                        $"[red]Error:[/] the last message [blue]#{last.Seq}[/] is from [blue]{Markup.Escape(last.From)}[/], not you. " +
                        "You can only drop your own last message; a human operator can override with [blue]--force[/].");
                    return 1;
                }
            }

            Stderr.MarkupLine($"[yellow]Will remove[/] [blue]#{last.Seq}[/] from [blue]{Markup.Escape(channel)}[/] (from [blue]{Markup.Escape(last.From)}[/]):");
            Stderr.MarkupLine($"  [grey]{Markup.Escape(Truncate(last.Text, 200))}[/]");

            if (!yes)
            {
                if (Console.IsInputRedirected)
                {
                    Stderr.MarkupLine("[red]Error:[/] Confirmation needs a terminal. Re-run with [blue]--yes[/] to drop non-interactively.");
                    return 1;
                }
                if (!Stderr.Prompt(new ConfirmationPrompt("Remove it?") { DefaultValue = false }))
                {
                    Stderr.MarkupLine("[yellow]Cancelled.[/]");
                    return 0;
                }
            }

            var popped = store.Pop(channel, last.Seq);
            var newMax = popped.Seq - 1;
            Stderr.MarkupLine($"[green]✓[/] removed [blue]#{popped.Seq}[/]; cursors past [blue]#{newMax}[/] rolled back.");
            return 0;
        }));

        return command;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
