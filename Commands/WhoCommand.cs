using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
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

        var command = new Command("who", "List the roles that have joined a channel (participants).")
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

            if (participants.Count == 0)
            {
                Stderr.MarkupLine($"[grey]No one has joined '{Markup.Escape(channel)}' yet.[/]");
                return Task.FromResult(0);
            }

            if (json)
            {
                foreach (var p in participants)
                    Console.WriteLine(JsonSerializer.Serialize(p));
                return Task.FromResult(0);
            }

            foreach (var p in participants)
                Console.WriteLine($"{p.Role}  ·  {p.MessageCount} msg  ·  last {FormatTime(p.LastActivity)}  ·  sid {ShortSid(p.Sid)}");
            return Task.FromResult(0);
        }));

        return command;
    }
}
