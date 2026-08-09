using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using ParleyCli.Serialization;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>List the roles that have joined a channel, with each one's message count and last activity.</summary>
public static class WhoCommand
{
    public static Command Create()
    {
        var channelArg = new Argument<string>("channel") { Description = "Channel name" };
        var jsonOpt = new Option<bool>("--json") { Description = "Emit participants as JSONL" };

        var command = new Command("list", "List the channel's active members.")
        {
            channelArg, jsonOpt
        };

        command.SetAction(Safe((pr, ct) =>
        {
            ApplyLogLevel(pr);
            var store = Cli.Services.GetRequiredService<ChannelStore>();

            var channel = ChannelStore.Validate("channel", pr.GetValue(channelArg)!);
            var json = pr.GetValue(jsonOpt);
            var participants = store.Participants(channel);
            var ownerRole = store.OwnerRole(channel);

            if (participants.Count == 0)
            {
                if (ownerRole is null)
                    Stderr.MarkupLine($"[grey]No one has joined '{Markup.Escape(channel)}' yet.[/]");
                else
                    Stderr.MarkupLine(
                        $"[grey]No active members. Owner role {Markup.Escape(ownerRole)} is vacant.[/]");
                return Task.FromResult(0);
            }

            if (ownerRole is not null && participants.All(p => !p.Owner))
                Stderr.MarkupLine(
                    $"[grey]Owner role {Markup.Escape(ownerRole)} is currently vacant.[/]");

            if (json)
            {
                foreach (var p in participants)
                    Console.WriteLine(JsonSerializer.Serialize(p, ParleyJsonContext.Default.Participant));
                return Task.FromResult(0);
            }

            foreach (var p in participants)
                Console.WriteLine($"{p.Role}{(p.Owner ? "  ·  owner" : "")}  ·  wake {p.Wake}  ·  {p.MessageCount} msg  ·  last {FormatTime(p.LastActivity)}  ·  sid {ShortSid(p.Sid)}");
            return Task.FromResult(0);
        }));

        return command;
    }
}
