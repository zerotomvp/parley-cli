using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>
/// Print one message in full by its <c>seq</c> — the untruncated companion to <c>log</c>'s preview.
/// Does not touch any cursor.
/// </summary>
public static class ShowCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string>("channel") { Description = "Channel name" };
        var seqArg = new Argument<int>("seq") { Description = "Message sequence number (the #N shown by log)" };
        var jsonOpt = new Option<bool>("--json") { Description = "Emit the message as a single JSON object" };

        var command = new Command("show", "Print one message in full by its seq (untruncated; does not advance any cursor).")
        {
            channelArg, seqArg, jsonOpt
        };

        command.SetAction(Safe((pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();

            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var seq = pr.GetValue(seqArg);
            var json = pr.GetValue(jsonOpt);

            var all = store.ReadAll(channel);
            var message = all.FirstOrDefault(m => m.Seq == seq);
            if (message is null)
            {
                Stderr.MarkupLine(all.Count == 0
                    ? $"[grey]Channel '{Markup.Escape(channel)}' has no messages.[/]"
                    : $"[red]Error:[/] no message #{seq} in '{Markup.Escape(channel)}' (seqs 1–{all.Count}).");
                return Task.FromResult(1);
            }

            PrintMessages(new[] { message }, json);
            return Task.FromResult(0);
        }));

        return command;
    }
}
