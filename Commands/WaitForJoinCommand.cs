using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

public static class WaitForJoinCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string>("channel") { Description = "Channel name" };
        var rolesArg = new Argument<string[]>("roles")
        {
            Description = "One or more roles that must be joined",
            Arity = ArgumentArity.OneOrMore
        };
        var timeoutOpt = new Option<int>("--timeout", "-t")
        {
            Description = "Seconds to wait; 0 or omitted waits indefinitely",
            DefaultValueFactory = _ => 0
        };

        var command = new Command("wait-for-join",
            "Wait until named roles have current owners without reading channel messages.")
        {
            channelArg, rolesArg, timeoutOpt
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();
            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var roles = pr.GetValue(rolesArg)!
                .Select(role => ChannelStore.Validate("role", role))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var timeout = pr.GetValue(timeoutOpt);
            if (timeout < 0)
                throw new ArgumentException("--timeout must be 0 or a positive number of seconds.");

            var present = roles.All(role => store.OwnerOf(channel, role) is not null);
            if (!present)
                Stderr.MarkupLine(timeout > 0
                    ? $"[cyan]Waiting up to {timeout}s for {Markup.Escape(string.Join(", ", roles))} to join…[/]"
                    : $"[cyan]Waiting for {Markup.Escape(string.Join(", ", roles))} to join (no timeout — Ctrl+C to stop)…[/]");

            var (satisfied, memberships) = await store.WaitForRoles(channel, roles, timeout, ct);
            if (!satisfied)
            {
                var joined = memberships.Count == 0
                    ? "none"
                    : string.Join(", ", memberships.Select(m => m.Role));
                Stderr.MarkupLine($"[yellow]Roles not joined within {timeout}s.[/] Present: {Markup.Escape(joined)}.");
                return 2;
            }

            foreach (var membership in memberships)
                Console.WriteLine($"{membership.Role}\tsid={membership.Sid}\twake={membership.Wake ?? "never"}");
            Stderr.MarkupLine($"[green]✓[/] {memberships.Count} requested role(s) joined [blue]{Markup.Escape(channel)}[/]");
            return 0;
        }));

        return command;
    }
}
