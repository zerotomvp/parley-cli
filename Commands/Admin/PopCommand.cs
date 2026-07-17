using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands.Admin;

/// <summary>
/// Remove the last message from a channel (highest seq) and roll back any cursor that had
/// read it. Prompts for confirmation unless --yes. Operator-facing undo, not for models.
/// </summary>
public static class PopCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string>("channel") { Description = "Channel name" };
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip the confirmation prompt" };

        var command = new Command("pop",
            "Remove the last message from a channel and roll back any cursor that had read it. Asks for confirmation unless --yes.")
        {
            channelArg, yesOpt
        };

        command.SetAction(Safe((pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();

            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var yes = pr.GetValue(yesOpt);

            var all = store.ReadAll(channel);
            if (all.Count == 0)
            {
                Stderr.MarkupLine($"[grey]Channel '{Markup.Escape(channel)}' has no messages to pop.[/]");
                return Task.FromResult(0);
            }

            var last = all[^1];
            Stderr.MarkupLine($"[yellow]Will remove[/] [blue]#{last.Seq}[/] from [blue]{Markup.Escape(channel)}[/] (from [blue]{Markup.Escape(last.From)}[/]):");
            Stderr.MarkupLine($"  [grey]{Markup.Escape(Truncate(last.Text, 200))}[/]");

            if (!yes)
            {
                if (Console.IsInputRedirected)
                {
                    Stderr.MarkupLine("[red]Error:[/] Confirmation needs a terminal. Re-run with [blue]--yes[/] to pop non-interactively.");
                    return Task.FromResult(1);
                }
                if (!Stderr.Prompt(new ConfirmationPrompt("Remove it?") { DefaultValue = false }))
                {
                    Stderr.MarkupLine("[yellow]Cancelled.[/]");
                    return Task.FromResult(0);
                }
            }

            var popped = store.Pop(channel, last.Seq);
            var newMax = popped.Seq - 1;
            Stderr.MarkupLine($"[green]✓[/] removed [blue]#{popped.Seq}[/]; cursors past [blue]#{newMax}[/] rolled back.");
            return Task.FromResult(0);
        }));

        return command;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
