using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Integrations;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

public static class ClaudeChannelCommand
{
    public static Command Create()
    {
        var command = new Command("claude-channel", "Run the one-way Claude Code MCP wake channel over stdio");
        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var sid = pr.GetValue(GlobalOptions.Sid)
                ?? Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID")
                ?? Environment.GetEnvironmentVariable("PARLEY_ID");
            if (string.IsNullOrWhiteSpace(sid))
                throw new ArgumentException(
                    "No Claude session id found. Claude Code normally supplies CLAUDE_CODE_SESSION_ID; use --sid for testing.");
            sid = Channels.ChannelStore.Validate("session id", sid);
            await Cli.Services.GetRequiredService<ClaudeChannelServer>().RunAsync(sid, ct);
            return 0;
        }));
        return command;
    }
}
