using System.CommandLine;

namespace ParleyCli.Commands.Admin;

/// <summary>
/// Maintenance commands intended for a human operator, not automated sessions —
/// e.g. removing a message a model posted by mistake.
/// </summary>
public static class AdminCommand
{
    public static Command Create()
    {
        var command = new Command("admin", "Maintenance commands for a human operator (not for automated sessions).");
        command.Subcommands.Add(PruneCommand.Create());
        return command;
    }
}
