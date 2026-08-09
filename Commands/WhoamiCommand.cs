using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using ParleyCli.Integrations;
using ParleyCli.Serialization;
using Spectre.Console;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

/// <summary>List every active channel role held by the current session.</summary>
public static class WhoamiCommand
{
    public static Command Create()
    {
        var jsonOpt = new Option<bool>("--json") { Description = "Emit memberships as JSONL" };
        var command = new Command("whoami", "List this session's active channel memberships.")
        {
            jsonOpt
        };

        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            HarnessCatalog.TraceDetection();
            var sid = ResolveSid(pr) ?? throw new ArgumentException(
                "No session ID detected. Pass --sid <id> or set PARLEY_ID for a manual session.");
            var store = Cli.Services.GetRequiredService<ChannelStore>();

            var detection = HarnessCatalog.InspectEnvironment();
            if (detection is { HasSessionId: true, Harness.Wake: "claude" })
                await Cli.Services.GetRequiredService<ClaudeSessionResolver>()
                    .TryRepairAllMembershipsAsync(sid, ct);

            var memberships = store.ActiveMembershipsOf(sid);
            if (memberships.Count == 0)
            {
                Stderr.MarkupLine($"[grey]No active channel memberships for sid {Markup.Escape(ShortSid(sid))}.[/]");
                return 0;
            }

            if (pr.GetValue(jsonOpt))
            {
                foreach (var membership in memberships)
                    Console.WriteLine(JsonSerializer.Serialize(
                        membership, ParleyJsonContext.Default.SessionMembership));
                return 0;
            }

            foreach (var membership in memberships)
                Console.WriteLine(
                    $"{membership.Channel}  ·  {membership.Role}" +
                    (membership.Owner ? "  ·  owner" : "") +
                    $"  ·  wake {membership.Wake}");
            return 0;
        }));

        return command;
    }
}
