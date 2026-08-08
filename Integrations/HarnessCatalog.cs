using Serilog;

namespace ParleyCli.Integrations;

/// <summary>The harness-specific metadata consumed by shared CLI command paths.</summary>
public sealed record HarnessDefinition(
    string Wake,
    string DisplayName,
    string SessionIdEnvironmentVariable,
    Type WakeClientType,
    string EndpointSubject,
    string? ProcessMarkerEnvironmentVariable = null);

public sealed record HarnessDetection(HarnessDefinition? Harness, bool HasSessionId)
{
    public bool IsPartial => Harness is not null && !HasSessionId;
}

public static class HarnessCatalog
{
    private static readonly HarnessDefinition[] Entries =
    [
        new("codex", "Codex", "CODEX_THREAD_ID", typeof(CodexWakeClient), "thread"),
        new("claude", "Claude Code", "CLAUDE_CODE_SESSION_ID", typeof(ClaudeWakeClient), "session"),
        new("pi", "Pi", "PI_SESSION_ID", typeof(PiWakeClient), "session", "PI_CODING_AGENT")
    ];

    public static IReadOnlyList<HarnessDefinition> All => Entries;

    public static HarnessDefinition? Find(string wake) =>
        Entries.FirstOrDefault(entry => entry.Wake.Equals(wake, StringComparison.OrdinalIgnoreCase));

    public static HarnessDefinition Get(string wake) => Find(wake)
        ?? throw new ArgumentException($"Unknown harness wake type '{wake}'.");

    public static HarnessDetection InspectEnvironment()
    {
        // A dedicated process marker wins over IDs inherited from a parent harness.
        // This matters when Pi itself was launched from a Codex or Claude tool shell.
        var marked = Entries.FirstOrDefault(entry =>
            entry.ProcessMarkerEnvironmentVariable is { } marker
            && IsEnabled(Environment.GetEnvironmentVariable(marker)));
        if (marked is not null)
            return new HarnessDetection(marked,
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(marked.SessionIdEnvironmentVariable)));

        var detected = Entries.FirstOrDefault(entry =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(entry.SessionIdEnvironmentVariable)));
        return new HarnessDetection(detected, detected is not null);
    }

    public static HarnessDefinition? Detect() => InspectEnvironment() is { HasSessionId: true } detection
        ? detection.Harness
        : null;

    public static string? ResolveSessionId() => Detect() is { } harness
        ? Environment.GetEnvironmentVariable(harness.SessionIdEnvironmentVariable)
        : null;

    public static string SupportedWakeValues =>
        string.Join(", ", Entries.Select(entry => entry.Wake).Append("never"));

    public static void TraceDetection()
    {
        var detection = InspectEnvironment();
        var markers = Entries
            .Select(entry => entry.ProcessMarkerEnvironmentVariable)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var variables = Entries
            .SelectMany(entry => new[]
            {
                entry.SessionIdEnvironmentVariable,
                entry.ProcessMarkerEnvironmentVariable
            })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Select(name => $"{name}={FormatEnvironmentValue(name, markers)}");

        var detected = detection switch
        {
            { IsPartial: true, Harness: { } harness } => $"partial:{harness.Wake}",
            { HasSessionId: true, Harness: { } harness } => harness.Wake,
            _ => "none"
        };
        Log.Verbose("[trace] harness detection; environment={Environment}; detected={Detected}",
            string.Join(",", variables), detected);
    }

    private static string FormatEnvironmentValue(string name, IReadOnlySet<string> markers)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return "<unset>";
        return markers.Contains(name) ? value : "<set>";
    }

    private static bool IsEnabled(string? value) =>
        value is not null && value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
