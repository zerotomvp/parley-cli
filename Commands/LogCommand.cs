using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>Print a channel's full transcript, without touching any cursor.</summary>
public static class LogCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string?>("channel")
        {
            Description = "Channel name (default: default)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var jsonOpt = new Option<bool>("--json") { Description = "Emit messages as JSONL" };

        var command = new Command("log", "Print the full transcript of a channel (does not advance any cursor).")
        {
            channelArg, jsonOpt
        };

        command.SetAction(Safe((pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();

            var channel = ResolveChannel(pr.GetValue(channelArg));
            var json = pr.GetValue(jsonOpt);

            var all = store.ReadAll(channel);
            if (all.Count == 0)
            {
                Stderr.MarkupLine($"[grey]Channel '{Markup.Escape(channel)}' has no messages.[/]");
                return Task.FromResult(0);
            }

            PrintMessages(all, json);
            return Task.FromResult(0);
        }));

        return command;
    }
}
