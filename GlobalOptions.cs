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
    /// This session's role on the channel — the addressable identity claimed via <c>join</c> and
    /// used to send/receive. Required (no auto-detected default): distinct sessions must pick
    /// distinct roles, and a shared auto-label would collide once more than two sessions join.
    /// </summary>
    public static readonly Option<string?> As = new Option<string?>("--as")
    {
        Description = "This session's role on the channel (required for participant and member-admin actions)",
        Recursive = true
    };

    /// <summary>
    /// Override for this session's unique session id (the role-ownership token + cursor key).
    /// Normally auto-detected from the runtime (CLAUDE_CODE_SESSION_ID / CODEX_THREAD_ID); this
    /// override — or the PARLEY_ID env var — is for manual/test use where no runtime injects one.
    /// </summary>
    public static readonly Option<string?> Sid = new Option<string?>("--sid")
    {
        Description = "Override the auto-detected session id (defaults to the runtime's id, or the PARLEY_ID env var)",
        Recursive = true
    };
}
