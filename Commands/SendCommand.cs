using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>
/// Post a message to a channel. The body comes from stdin (so a model can write multi-line
/// messages) or from an optional inline argument. Delivery is explicit: address specific roles
/// with --to, or everyone with --broadcast. The assigned seq is printed to stdout. With --wait,
/// blocks after sending until a message addressed to me arrives and prints it.
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
        var toOpt = new Option<string?>("--to")
        {
            Description = "Comma-separated recipient roles (e.g. reviewer,author). Mutually exclusive with --broadcast."
        };
        var broadcastOpt = new Option<bool>("--broadcast")
        {
            Description = "Send to every role on the channel. Mutually exclusive with --to."
        };
        var waitOpt = new Option<bool>("--wait", "-w")
        {
            Description = "After sending, block until a message addressed to me arrives and print it"
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
        var jsonOpt = new Option<bool>("--json") { Description = "Emit the seq (and any --wait reply) as JSON/JSONL" };

        var command = new Command("send", "Post a message to a channel (body from stdin, or -m). Requires --to or --broadcast.")
        {
            channelArg, messageOpt, toOpt, broadcastOpt, waitOpt, timeoutOpt, expectNewOpt, closeOpt, jsonOpt
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

            // Delivery: exactly one of --to / --broadcast.
            var broadcast = pr.GetValue(broadcastOpt);
            var toRaw = pr.GetValue(toOpt);
            var toRoles = string.IsNullOrWhiteSpace(toRaw)
                ? Array.Empty<string>()
                : toRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(r => ChannelStore.Validate("recipient role", r)).ToArray();
            if (toRoles.Length > 0 == broadcast)
            {
                Stderr.MarkupLine("[red]Error:[/] Specify recipients with [blue]--to <roles>[/] or [blue]--broadcast[/] (exactly one).");
                return 1;
            }

            // Must have joined as this role from this session before sending.
            store.VerifyMembership(channel, me.Role, me.Sid);

            // Best-effort: warn about addressed roles nobody has joined yet (likely a typo).
            foreach (var r in toRoles)
                if (store.OwnerOf(channel, r) is null)
                    Stderr.MarkupLine($"[yellow]Note:[/] role [blue]{Markup.Escape(r)}[/] hasn't joined [blue]{Markup.Escape(channel)}[/] yet — it won't see this until it does.");

            var inline = pr.GetValue(messageOpt);
            var text = inline ?? (Console.IsInputRedirected ? await Console.In.ReadToEndAsync(ct) : "");
            text = text.TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(text))
            {
                Stderr.MarkupLine("[red]Error:[/] Empty message. Pipe text via stdin or pass it inline.");
                return 1;
            }

            var sent = store.Append(channel, me.Role, me.Sid, text, toRoles, broadcast, expectNew, close);

            // The assigned seq goes to stdout so a caller can capture it (e.g. to drop it later).
            Console.WriteLine(json ? JsonSerializer.Serialize(new { seq = sent.Seq }) : sent.Seq.ToString());

            var dest = broadcast ? "all" : string.Join(",", toRoles);
            Stderr.MarkupLine($"[green]✓[/] sent [blue]#{sent.Seq}[/] to [blue]{Markup.Escape(channel)}[/] as [blue]{Markup.Escape(me.Role)}[/] → [blue]{Markup.Escape(dest)}[/]{(close ? " [yellow](closed)[/]" : "")}");

            if (close && wait)
                Stderr.MarkupLine("[grey]Note: --close means no reply is expected; --wait would block with nothing to receive.[/]");

            if (!wait) return 0;

            Stderr.MarkupLine(timeout > 0
                ? $"[cyan]Waiting up to {timeout}s for a reply…[/]"
                : "[cyan]Waiting for a reply (no timeout — Ctrl+C to stop)…[/]");
            var (satisfied, snapshot) = await store.WaitForPeer(channel, me.Sid, me.Role, sent.Seq, timeout, ct);

            // Only reachable with a finite --timeout; an indefinite wait never returns unsatisfied.
            if (!satisfied)
            {
                Stderr.MarkupLine($"[yellow]No reply within {timeout}s.[/] Message is delivered — run [blue]parley recv {Markup.Escape(channel)} --as {Markup.Escape(me.Role)} --wait[/] to keep waiting.");
                return 2; // timeout: no relevant reply yet
            }

            var cursor = store.GetCursor(channel, me.Sid);
            var unread = snapshot.Where(m => m.Seq > cursor && m.Sid != me.Sid && m.IsFor(me.Role)).ToList();
            PrintMessages(unread, json);
            store.SetCursor(channel, me.Sid, snapshot[^1].Seq);
            NoteIfClosed(unread);
            return 0;
        }));

        return command;
    }
}
