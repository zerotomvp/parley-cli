using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>Print a channel's full transcript, without touching any cursor.</summary>
public static class LogCommand
{
    /// <summary>Chars of the first body line shown in the human-readable preview before it's cut off.</summary>
    private const int PreviewChars = 200;

    public static Command Create()
    {
        var channelArg = new Argument<string>("channel") { Description = "Channel name" };
        var limitOpt = new Option<int>("--limit", "-n")
        {
            Description = "Show only the most recent N messages (0 = all). Default 10.",
            DefaultValueFactory = _ => 10
        };
        var jsonOpt = new Option<bool>("--json") { Description = "Emit messages as JSONL (full, untruncated bodies; includes each sender's session id)" };

        var command = new Command("log", "Print a channel's transcript — recent messages, bodies truncated to a preview (does not advance any cursor).")
        {
            channelArg, limitOpt, jsonOpt
        };

        command.SetAction(Safe((pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();

            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var limit = pr.GetValue(limitOpt);
            var json = pr.GetValue(jsonOpt);

            var all = store.ReadAll(channel);
            if (all.Count == 0)
            {
                Stderr.MarkupLine($"[grey]Channel '{Markup.Escape(channel)}' has no messages.[/]");
                return Task.FromResult(0);
            }

            var shown = limit > 0 && all.Count > limit ? all.Skip(all.Count - limit).ToList() : all;
            if (shown.Count < all.Count)
                Stderr.MarkupLine(
                    $"[grey]… {all.Count - shown.Count} older message(s) hidden — 'parley messages log {Markup.Escape(channel)} --limit 0' for all[/]");

            // JSON stays full (machine-readable); human output previews the head and marks any cut-off.
            PrintMessages(shown, json, previewChars: json ? null : PreviewChars, channel: channel);
            return Task.FromResult(0);
        }));

        return command;
    }
}
