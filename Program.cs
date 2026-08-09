using System.CommandLine;
using System.CommandLine.Completions;
using ParleyCli;
using ParleyCli.Commands;
using ParleyCli.Commands.Admin;
using ParleyCli.Commands.Integrations;
using ParleyCli.Commands.Members;
using ParleyCli.Commands.Messages;
using ParleyCli.Logging;
using ParleyCli.Updates;
using Serilog;
using Serilog.Core;

var levelSwitch = new LoggingLevelSwitch(LoggingConfiguration.InitialLevel);
Log.Logger = LoggingConfiguration.CreateLogger(levelSwitch);

if (LoggingConfiguration.ConfigurationWarning is { } configurationWarning)
    Log.Warning("{ConfigurationWarning}", configurationWarning);

if (LoggingConfiguration.TraceEnabled)
    Log.Verbose("[trace] diagnostics enabled by {Source}; version={Version}",
        LoggingConfiguration.TraceSource,
        ParleyVersion.Display);

Cli.ConfigureServices(s => s.AddParleyServices(levelSwitch));

var rootCommand = new RootCommand(
    "parley: a role-addressed message channel so any number of agent sessions can coordinate over a shared transcript")
{
    GlobalOptions.LogLevel,
    GlobalOptions.As,
    GlobalOptions.Sid
};

rootCommand.Directives.Add(new SuggestDirective());

rootCommand.Subcommands.Add(JoinCommand.Create());
rootCommand.Subcommands.Add(WhoamiCommand.Create());
rootCommand.Subcommands.Add(LeaveCommand.Create());
rootCommand.Subcommands.Add(SendCommand.Create());
rootCommand.Subcommands.Add(RecvCommand.Create());
rootCommand.Subcommands.Add(DropCommand.Create());
rootCommand.Subcommands.Add(MembersCommand.Create());
rootCommand.Subcommands.Add(MessagesCommand.Create());
rootCommand.Subcommands.Add(IntegrationsCommand.Create());
rootCommand.Subcommands.Add(AdminCommand.Create());

try
{
    var parseResult = rootCommand.Parse(args);
    await UpdateChecker.CheckAndNotifyAsync(parseResult.CommandResult.Command.Name, Console.Error);
    return await parseResult.InvokeAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled error");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
