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
        var channelArg = new Argument<string>("channel")
        {
            Description = "Channel name. Add a random 5-letter suffix to avoid collisions, e.g. review-xyzab."
        };
        var messageOpt = new Option<string?>("--message", "-m")
        {
            Description = "Message body (omit to read from stdin)"
        };
        var waitOpt = new Option<bool>("--wait", "-w")
        {
            Description = "After sending, block until another session replies and print the reply"
        };
        var timeoutOpt = new Option<int>("--timeout", "-t")
        {
            Description = "Seconds to bound the wait (with --wait); 0 or omitted = wait indefinitely until a reply arrives",
            DefaultValueFactory = _ => 0
        };
        var expectNewOpt = new Option<bool>("--expect-new")
        {
            Description = "Assert the channel is fresh (empty, or one opener message per other session) before sending; fails if the name already has a conversation"
        };
        var closeOpt = new Option<bool>("--close")
        {
            Description = "Mark this message final — you're ending the exchange and expect no reply. Send it without --wait."
        };
        var jsonOpt = new Option<bool>("--json") { Description = "Emit the reply as JSONL (with --wait)" };

        var command = new Command("send", "Post a message to a channel (body from stdin, or -m).")
        {
            channelArg, messageOpt, waitOpt, timeoutOpt, expectNewOpt, closeOpt, jsonOpt
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();

            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var me = ResolveIdentity(pr);
            var wait = pr.GetValue(waitOpt);
            var timeout = pr.GetValue(timeoutOpt);
            var expectNew = pr.GetValue(expectNewOpt);
            var close = pr.GetValue(closeOpt);
            var json = pr.GetValue(jsonOpt);

            var inline = pr.GetValue(messageOpt);
            var text = inline ?? (Console.IsInputRedirected ? await Console.In.ReadToEndAsync(ct) : "");
            text = text.TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(text))
            {
                Stderr.MarkupLine("[red]Error:[/] Empty message. Pipe text via stdin or pass it inline.");
                return 1;
            }

            var sent = store.Append(channel, me.Label, me.Sid, text, expectNew, close);
            Stderr.MarkupLine($"[green]✓[/] sent [blue]#{sent.Seq}[/] to [blue]{Markup.Escape(channel)}[/] as [blue]{Markup.Escape(me.Label)}[/]{(close ? " [yellow](closed)[/]" : "")}");

            if (close && wait)
                Stderr.MarkupLine("[grey]Note: --close means no reply is expected; --wait would block with nothing to receive.[/]");

            if (!wait) return 0;

            Stderr.MarkupLine(timeout > 0
                ? $"[cyan]Waiting up to {timeout}s for a reply…[/]"
                : "[cyan]Waiting for a reply (no timeout — Ctrl+C to stop)…[/]");
            var (satisfied, snapshot) = await store.WaitForPeer(channel, me.Sid, sent.Seq, timeout, ct);

            // Only reachable with a finite --timeout; an indefinite wait never returns unsatisfied.
            if (!satisfied)
            {
                Stderr.MarkupLine($"[yellow]No reply within {timeout}s.[/] Message is delivered — run [blue]parley recv {Markup.Escape(channel)} --wait[/] to keep waiting.");
                return 2; // timeout: no other session has replied yet
            }

            var cursor = store.GetCursor(channel, me.Sid);
            var unread = snapshot.Where(m => m.Seq > cursor && m.Sid != me.Sid).ToList();
            PrintMessages(unread, json);
            store.SetCursor(channel, me.Sid, snapshot[^1].Seq);
            NoteIfClosed(unread);
            return 0;
        }));

        return command;
    }
}
