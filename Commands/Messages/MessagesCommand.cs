using System.CommandLine;

namespace ParleyCli.Commands.Messages;

public static class MessagesCommand
{
    public static Command Create()
    {
        var command = new Command("messages", "Inspect the durable channel transcript.");
        command.Subcommands.Add(LogCommand.Create());
        command.Subcommands.Add(ShowCommand.Create());
        return command;
    }
}
