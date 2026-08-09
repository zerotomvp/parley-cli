using System.CommandLine;

namespace ParleyCli.Commands.Integrations;

public static class IntegrationsCommand
{
    public static Command Create()
    {
        var command = new Command("integrations", "Run coding-harness wake integrations.");
        command.Subcommands.Add(ClaudeChannelCommand.Create());
        command.Subcommands.Add(PiChannelCommand.Create());
        return command;
    }
}
