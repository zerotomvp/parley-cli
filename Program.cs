using System.CommandLine;
using System.CommandLine.Completions;
using ParleyCli;
using ParleyCli.Commands;
using ParleyCli.Logging;
using Serilog;
using Serilog.Core;

var levelSwitch = new LoggingLevelSwitch();
Log.Logger = LoggingConfiguration.CreateLogger(levelSwitch);

Cli.ConfigureServices(s => s.AddParleyServices(levelSwitch));

var rootCommand = new RootCommand(
    "parley: a two-party message channel so a Claude Code and a Codex session can coordinate over a shared transcript")
{
    GlobalOptions.LogLevel,
    GlobalOptions.As
};

rootCommand.Directives.Add(new SuggestDirective());

rootCommand.Subcommands.Add(SendCommand.Create());
rootCommand.Subcommands.Add(RecvCommand.Create());
rootCommand.Subcommands.Add(LogCommand.Create());

try
{
    return await rootCommand.Parse(args).InvokeAsync();
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
