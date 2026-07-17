using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>
/// Read unread peer messages from a channel and advance this session's cursor.
/// With --wait, blocks until at least one unread peer message arrives (or timeout).
/// </summary>
public static class RecvCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string>("channel") { Description = "Channel name" };
        var waitOpt = new Option<bool>("--wait", "-w")
        {
            Description = "Block until an unread peer message arrives"
        };
        var timeoutOpt = new Option<int>("--timeout", "-t")
        {
            Description = "Seconds to wait (with --wait)",
            DefaultValueFactory = _ => 90
        };
        var jsonOpt = new Option<bool>("--json") { Description = "Emit messages as JSONL" };

        var command = new Command("recv", "Read unread peer messages (optionally waiting for one to arrive).")
        {
            channelArg, waitOpt, timeoutOpt, jsonOpt
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();

            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var me = ResolveId(pr);
            var wait = pr.GetValue(waitOpt);
            var timeout = pr.GetValue(timeoutOpt);
            var json = pr.GetValue(jsonOpt);

            var cursor = store.GetCursor(channel, me);
            var snapshot = store.ReadAll(channel);
            var unread = snapshot.Where(m => m.Seq > cursor && m.From != me).ToList();

            if (unread.Count == 0 && wait)
            {
                Stderr.MarkupLine($"[cyan]Waiting up to {timeout}s for a message…[/]");
                var (satisfied, waited) = await store.WaitForPeer(channel, me, cursor, timeout, ct);
                if (!satisfied)
                {
                    Stderr.MarkupLine($"[yellow]No new message within {timeout}s.[/] Run again to keep waiting.");
                    return 2; // timeout: nothing yet
                }
                snapshot = waited;
                unread = snapshot.Where(m => m.Seq > cursor && m.From != me).ToList();
            }

            if (unread.Count == 0)
            {
                Stderr.MarkupLine("[grey]No new messages.[/]");
                return 0;
            }

            PrintMessages(unread, json);
            store.SetCursor(channel, me, snapshot[^1].Seq);
            Stderr.MarkupLine($"[green]✓[/] {unread.Count} message(s) from the peer");
            return 0;
        }));

        return command;
    }
}
