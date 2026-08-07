using System.CommandLine;
using System.CommandLine.Completions;
using ParleyCli;
using ParleyCli.Commands;
using ParleyCli.Commands.Admin;
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
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown");

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
rootCommand.Subcommands.Add(SendCommand.Create());
rootCommand.Subcommands.Add(RecvCommand.Create());
rootCommand.Subcommands.Add(WaitForJoinCommand.Create());
rootCommand.Subcommands.Add(WhoCommand.Create());
rootCommand.Subcommands.Add(LogCommand.Create());
rootCommand.Subcommands.Add(ShowCommand.Create());
rootCommand.Subcommands.Add(DropCommand.Create());
rootCommand.Subcommands.Add(ClaudeChannelCommand.Create());
rootCommand.Subcommands.Add(PiChannelCommand.Create());
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
