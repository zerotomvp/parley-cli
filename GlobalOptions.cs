using System.CommandLine;

namespace ParleyCli;

public static class GlobalOptions
{
    public static readonly Option<string> LogLevel = new Option<string>("--log-level")
    {
        Description = "Log level (trace, debug, info, warn, error)",
        DefaultValueFactory = _ => "info",
        Recursive = true
    };

    /// <summary>
    /// Who I am on the channel. Resolved from --as, else the PARLEY_ID env var.
    /// Used to distinguish my own messages from the peer's on read.
    /// </summary>
    public static readonly Option<string?> As = new Option<string?>("--as")
    {
        Description = "This session's participant id (defaults to the PARLEY_ID env var)",
        Recursive = true
    };
}
