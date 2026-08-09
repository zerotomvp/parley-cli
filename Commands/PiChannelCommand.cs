using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ParleyCli.Channels;
using ParleyCli.Integrations;
using static ParleyCli.Commands.CommandHelpers;

namespace ParleyCli.Commands;

public static class PiChannelCommand
{
    public static Command Create()
    {
        var command = new Command("pi", "Run the Pi extension wake bridge over JSONL stdio");
        command.SetAction(Safe(async (pr, ct) =>
        {
            ApplyLogLevel(pr);
            var sid = pr.GetValue(GlobalOptions.Sid)
                ?? Environment.GetEnvironmentVariable(
                    HarnessCatalog.Get("pi").SessionIdEnvironmentVariable)
                ?? Environment.GetEnvironmentVariable("PARLEY_ID");
            if (string.IsNullOrWhiteSpace(sid))
                throw new ArgumentException(
                    "No Pi session id found. Pi normally supplies PI_SESSION_ID; use --sid for testing.");
            sid = ChannelStore.Validate("session id", sid);
            await Cli.Services.GetRequiredService<PiChannelServer>().RunAsync(sid, ct);
            return 0;
        }));
        return command;
    }
}
