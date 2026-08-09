using System.CommandLine;

namespace ParleyCli.Commands.Members;

public static class MembersCommand
{
    public static Command Create()
    {
        var command = new Command("members", "Inspect and administer channel membership.");
        command.Subcommands.Add(WhoCommand.Create());
        command.Subcommands.Add(WaitForJoinCommand.Create());
        command.Subcommands.Add(RemoveMemberCommand.Create());
        return command;
    }
}
