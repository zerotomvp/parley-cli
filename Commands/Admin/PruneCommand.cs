using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands.Admin;

/// <summary>
/// Delete channels that have been idle (no new message) longer than a cutoff — cleanup for
/// finished conversations. Lists what it will remove and confirms unless --yes. Operator-facing.
/// </summary>
public static class PruneCommand
{
    public static Command Create()
    {
        var daysOpt = new Option<int>("--days")
        {
            Description = "Idle-days cutoff: delete channels whose last message is older than this",
            DefaultValueFactory = _ => 30
        };
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip the confirmation prompt" };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "List what would be deleted, delete nothing" };

        var command = new Command("prune",
            "Delete channels idle longer than --days (default 30). Lists them and asks for confirmation unless --yes.")
        {
            daysOpt, yesOpt, dryRunOpt
        };

        command.SetAction(Safe((pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();

            var days = pr.GetValue(daysOpt);
            var yes = pr.GetValue(yesOpt);
            var dryRun = pr.GetValue(dryRunOpt);

            if (days < 0)
            {
                Stderr.MarkupLine("[red]Error:[/] --days must be zero or positive.");
                return Task.FromResult(1);
            }

            var now = DateTimeOffset.UtcNow;
            var cutoff = now - TimeSpan.FromDays(days);
            var stale = store.ListChannels()
                .Select(ch => { var (count, last) = store.ChannelActivity(ch); return (ch, count, last); })
                .Where(x => x.last < cutoff)
                .OrderBy(x => x.last)
                .ToList();

            if (stale.Count == 0)
            {
                Stderr.MarkupLine($"[grey]No channels idle longer than {days} day(s).[/]");
                return Task.FromResult(0);
            }

            Stderr.MarkupLine($"[yellow]{stale.Count} channel(s) idle > {days} day(s):[/]");
            foreach (var (ch, count, last) in stale)
            {
                var age = (int)(now - last).TotalDays;
                Stderr.MarkupLine($"  [blue]{Markup.Escape(ch)}[/] [grey]({count} msg, last {age}d ago)[/]");
            }

            if (dryRun)
            {
                Stderr.MarkupLine("[grey]Dry run — nothing deleted.[/]");
                return Task.FromResult(0);
            }

            if (!yes)
            {
                if (Console.IsInputRedirected)
                {
                    Stderr.MarkupLine("[red]Error:[/] Confirmation needs a terminal. Re-run with [blue]--yes[/] (or [blue]--dry-run[/] to preview).");
                    return Task.FromResult(1);
                }
                if (!Stderr.Prompt(new ConfirmationPrompt($"Delete these {stale.Count} channel(s)?") { DefaultValue = false }))
                {
                    Stderr.MarkupLine("[yellow]Cancelled.[/]");
                    return Task.FromResult(0);
                }
            }

            foreach (var (ch, _, _) in stale)
                store.DeleteChannel(ch);

            Stderr.MarkupLine($"[green]✓[/] deleted {stale.Count} channel(s).");
            return Task.FromResult(0);
        }));

        return command;
    }
}
