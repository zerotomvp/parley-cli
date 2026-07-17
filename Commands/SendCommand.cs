using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>
/// Post a message to a channel. The body comes from stdin (so a model can write
/// multi-line messages) or from an optional inline argument. With --wait, blocks
/// after sending until the peer replies and prints that reply.
/// </summary>
public static class SendCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string?>("channel")
        {
            Description = "Channel name (default: default)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var messageOpt = new Option<string?>("--message", "-m")
        {
            Description = "Message body (omit to read from stdin)"
        };
        var waitOpt = new Option<bool>("--wait", "-w")
        {
            Description = "After sending, block until the peer replies and print the reply"
        };
        var timeoutOpt = new Option<int>("--timeout", "-t")
        {
            Description = "Seconds to wait for a reply (with --wait)",
            DefaultValueFactory = _ => 90
        };
        var jsonOpt = new Option<bool>("--json") { Description = "Emit the reply as JSONL (with --wait)" };

        var command = new Command("send", "Post a message to a channel (body from stdin, or -m).")
        {
            channelArg, messageOpt, waitOpt, timeoutOpt, jsonOpt
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();

            var channel = ResolveChannel(pr.GetValue(channelArg));
            var me = ResolveId(pr);
            var wait = pr.GetValue(waitOpt);
            var timeout = pr.GetValue(timeoutOpt);
            var json = pr.GetValue(jsonOpt);

            var inline = pr.GetValue(messageOpt);
            var text = inline ?? (Console.IsInputRedirected ? await Console.In.ReadToEndAsync(ct) : "");
            text = text.TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(text))
            {
                Stderr.MarkupLine("[red]Error:[/] Empty message. Pipe text via stdin or pass it inline.");
                return 1;
            }

            var sent = store.Append(channel, me, text);
            Stderr.MarkupLine($"[green]✓[/] sent [blue]#{sent.Seq}[/] to [blue]{Markup.Escape(channel)}[/] as [blue]{Markup.Escape(me)}[/]");

            if (!wait) return 0;

            Stderr.MarkupLine($"[cyan]Waiting up to {timeout}s for a reply…[/]");
            var (satisfied, snapshot) = await store.WaitForPeer(channel, me, sent.Seq, timeout, ct);

            if (!satisfied)
            {
                Stderr.MarkupLine($"[yellow]No reply within {timeout}s.[/] Message is delivered — run [blue]parley recv {Markup.Escape(channel)} --wait[/] to keep waiting.");
                return 2; // timeout: peer hasn't replied yet
            }

            var cursor = store.GetCursor(channel, me);
            var unread = snapshot.Where(m => m.Seq > cursor && m.From != me).ToList();
            PrintMessages(unread, json);
            store.SetCursor(channel, me, snapshot[^1].Seq);
            return 0;
        }));

        return command;
    }
}
