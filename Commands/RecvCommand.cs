using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using ParleyCli.Integrations;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>
/// Read peer messages after the model's explicit last-seen checkpoint and advance the CLI's
/// diagnostic delivery cursor.
/// With --wait, blocks until at least one unread peer message arrives (or timeout).
/// </summary>
public static class RecvCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string>("channel")
        {
            Description = "Channel name. Add a random 5-letter suffix to avoid collisions, e.g. review-xyzab."
        };
        var waitOpt = new Option<bool>("--wait", "-w")
        {
            Description = "Block until an unread message from another session arrives"
        };
        var lastSeenOpt = new Option<int>("--last-seen")
        {
            Description = "Highest transcript sequence actually present in this model's context (use 0 if none)",
            Required = true
        };
        var timeoutOpt = new Option<int>("--timeout", "-t")
        {
            Description = "Seconds to bound the wait (with --wait); 0 or omitted = wait indefinitely until a message arrives",
            DefaultValueFactory = _ => 0
        };
        var jsonOpt = new Option<bool>("--json") { Description = "Emit messages as JSONL" };

        var command = new Command("recv", "Read unread peer messages (optionally waiting for one to arrive).")
        {
            channelArg, lastSeenOpt, waitOpt, timeoutOpt, jsonOpt
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();
            var claudeSessions = Cli.Services.GetRequiredService<ClaudeSessionResolver>();

            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var me = ResolveIdentity(pr);
            var wait = pr.GetValue(waitOpt);
            var timeout = pr.GetValue(timeoutOpt);
            var json = pr.GetValue(jsonOpt);
            var lastSeen = pr.GetValue(lastSeenOpt);

            if (lastSeen < 0)
            {
                Stderr.MarkupLine("[red]Error:[/] --last-seen must be 0 or a positive transcript sequence.");
                return 1;
            }

            // Must have joined as this role from this session before receiving.
            await claudeSessions.TryRepairMembershipAsync(channel, me.Role, me.Sid, ct);
            store.VerifyMembership(channel, me.Role, me.Sid);

            var snapshot = store.ReadAll(channel);
            if (lastSeen > snapshot.Count)
            {
                Stderr.MarkupLine($"[red]Error:[/] --last-seen {lastSeen} is ahead of the transcript (latest is {snapshot.Count}).");
                return 1;
            }

            var emittedThrough = store.GetCursor(channel, me.Sid);
            if (lastSeen < emittedThrough)
                Stderr.MarkupLine($"[yellow]Note:[/] replaying from model checkpoint [blue]{lastSeen}[/]; this CLI previously emitted through [blue]{emittedThrough}[/].");

            var unread = snapshot.Where(m => m.Seq > lastSeen
                && !store.IsSameSession(channel, m.Sid, me.Sid) && m.IsFor(me.Role)).ToList();

            if (unread.Count == 0 && wait)
            {
                Stderr.MarkupLine(timeout > 0
                    ? $"[cyan]Waiting up to {timeout}s for a message…[/]"
                    : "[cyan]Waiting for a message (no timeout — Ctrl+C to stop)…[/]");
                var (satisfied, waited) = await store.WaitForPeer(channel, me.Sid, me.Role, lastSeen, timeout, ct);
                // Only reachable with a finite --timeout; an indefinite wait never returns unsatisfied.
                if (!satisfied)
                {
                    Stderr.MarkupLine($"[yellow]No new message within {timeout}s.[/] Run again to keep waiting.");
                    return 2; // timeout: nothing yet
                }
                snapshot = waited;
                unread = snapshot.Where(m => m.Seq > lastSeen
                    && !store.IsSameSession(channel, m.Sid, me.Sid) && m.IsFor(me.Role)).ToList();
            }

            if (unread.Count == 0)
            {
                Stderr.MarkupLine("[grey]No new messages.[/]");
                return 0;
            }

            PrintMessages(unread, json);
            store.SetCursor(channel, me.Sid, Math.Max(emittedThrough, snapshot[^1].Seq));
            var checkpoint = unread[^1].Seq;
            Stderr.MarkupLine($"[cyan]Checkpoint:[/] {checkpoint}");
            Stderr.MarkupLine($"[grey]Next receive: parley recv {Markup.Escape(channel)} --as {Markup.Escape(me.Role)} --last-seen {checkpoint} --wait[/]");
            Stderr.MarkupLine($"[green]✓[/] {unread.Count} message(s) from other session(s)");
            NoteIfClosed(unread);
            return 0;
        }));

        return command;
    }
}
