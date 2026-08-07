namespace ParleyCli.Integrations;

/// <summary>The harness-specific metadata consumed by shared CLI command paths.</summary>
public sealed record HarnessDefinition(
    string Wake,
    string SessionIdEnvironmentVariable,
    Type WakeClientType,
    string EndpointSubject,
    string? ProcessMarkerEnvironmentVariable = null);

public static class HarnessCatalog
{
    private static readonly HarnessDefinition[] Entries =
    [
        new("codex", "CODEX_THREAD_ID", typeof(CodexWakeClient), "thread"),
        new("claude", "CLAUDE_CODE_SESSION_ID", typeof(ClaudeWakeClient), "session"),
        new("pi", "PI_SESSION_ID", typeof(PiWakeClient), "session", "PI_CODING_AGENT")
    ];

    public static IReadOnlyList<HarnessDefinition> All => Entries;

    public static HarnessDefinition? Find(string wake) =>
        Entries.FirstOrDefault(entry => entry.Wake.Equals(wake, StringComparison.OrdinalIgnoreCase));

    public static HarnessDefinition Get(string wake) => Find(wake)
        ?? throw new ArgumentException($"Unknown harness wake type '{wake}'.");

    public static HarnessDefinition? Detect()
    {
        // A dedicated process marker wins over IDs inherited from a parent harness.
        // This matters when Pi itself was launched from a Codex or Claude tool shell.
        var marked = Entries.FirstOrDefault(entry =>
            entry.ProcessMarkerEnvironmentVariable is { } marker
            && IsEnabled(Environment.GetEnvironmentVariable(marker))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(entry.SessionIdEnvironmentVariable)));
        return marked ?? Entries.FirstOrDefault(entry =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(entry.SessionIdEnvironmentVariable)));
    }

    public static string? ResolveSessionId() => Detect() is { } harness
        ? Environment.GetEnvironmentVariable(harness.SessionIdEnvironmentVariable)
        : null;

    public static string SupportedWakeValues =>
        string.Join(", ", Entries.Select(entry => entry.Wake).Append("never"));

    private static bool IsEnabled(string? value) =>
        value is not null && value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
