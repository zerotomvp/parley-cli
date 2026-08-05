using System.Text.Json;
using ParleyCli.Serialization;
using Serilog;

namespace ParleyCli.Integrations;

/// <summary>
/// Announces a Claude channel endpoint independently of Parley conversation membership. Claude's
/// public session UUID changes across /clear, while its process correlation remains stable.
/// </summary>
public sealed class ClaudeEndpointRegistry(ClaudeAgentDiscovery discovery)
{
    private readonly string _directory = Path.Combine(ParleyHome(), "runtime", "claude");

    public async Task<ClaudeEndpointRegistrationHandle?> RegisterAsync(
        string endpointSid, CancellationToken ct)
    {
        Log.Verbose("[trace] Claude endpoint registration discovery begin; endpointSid={EndpointSid}", endpointSid);
        var agents = await discovery.ListAsync(ct);
        Log.Verbose("[trace] Claude endpoint registration discovery completed; endpointSid={EndpointSid} agentCount={AgentCount}",
            endpointSid, agents?.Count ?? -1);
        var agent = agents?.SingleOrDefault(a => a.SessionId == endpointSid);
        if (agent is null)
        {
            Log.Verbose("[trace] Claude endpoint registration skipped; endpointSid={EndpointSid} process correlation unavailable",
                endpointSid);
            return null;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            Sweep(agents!);
            var registration = new ClaudeEndpointRegistration(
                Guid.NewGuid().ToString("N"), agent.Pid, agent.StartedAt,
                endpointSid, Environment.ProcessId, DateTimeOffset.UtcNow.ToString("o"));
            WriteAtomic(PathFor(agent.Pid, agent.StartedAt), registration);
            Log.Verbose("[trace] Claude endpoint registered; claudePid={ClaudePid} claudeStartedAt={ClaudeStartedAt} endpointSid={EndpointSid} channelServerPid={ChannelServerPid}",
                registration.ClaudePid, registration.ClaudeStartedAt,
                registration.EndpointSid, registration.ChannelServerPid);
            return new(this, registration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Verbose(ex, "[trace] Claude endpoint registration failed; endpointSid={EndpointSid}", endpointSid);
            return null;
        }
    }

    public ClaudeEndpointRegistration? Find(ClaudeProcessCorrelation process)
    {
        var path = PathFor(process.Pid, process.StartedAt);
        var registration = Read(path);
        if (registration is null)
        {
            TryDelete(path);
            return null;
        }
        if (registration.ClaudePid == process.Pid
            && registration.ClaudeStartedAt == process.StartedAt)
            return registration;
        TryDelete(path);
        return null;
    }

    public void UpdateEndpoint(ClaudeEndpointRegistration registration, string endpointSid)
    {
        var path = PathFor(registration.ClaudePid, registration.ClaudeStartedAt);
        var current = Read(path);
        if (current?.RegistrationId != registration.RegistrationId) return;
        WriteAtomic(path, current with { EndpointSid = endpointSid });
    }

    public void Remove(ClaudeEndpointRegistration registration)
    {
        var path = PathFor(registration.ClaudePid, registration.ClaudeStartedAt);
        if (Read(path)?.RegistrationId == registration.RegistrationId)
            TryDelete(path);
    }

    private void Sweep(IReadOnlyList<ClaudeAgentInfo> agents)
    {
        var active = agents.Select(a => (a.Pid, a.StartedAt)).ToHashSet();
        foreach (var path in Directory.GetFiles(_directory, "*.json"))
        {
            var registration = Read(path);
            if (registration is null
                || !active.Contains((registration.ClaudePid, registration.ClaudeStartedAt)))
                TryDelete(path);
        }
        foreach (var path in Directory.GetFiles(_directory, "*.tmp"))
            TryDelete(path);
    }

    private ClaudeEndpointRegistration? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(path), ParleyJsonContext.Default.ClaudeEndpointRegistration);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Verbose(ex, "[trace] Claude endpoint registration is unreadable; path={Path}", path);
            return null;
        }
    }

    private static void WriteAtomic(string path, ClaudeEndpointRegistration registration)
    {
        var temp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(
                registration, ParleyJsonContext.Default.ClaudeEndpointRegistration));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) TryDelete(temp);
        }
    }

    private string PathFor(int pid, long startedAt) =>
        Path.Combine(_directory, $"{pid}-{startedAt}.json");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Verbose(ex, "[trace] Failed to remove stale Claude endpoint registration; path={Path}", path);
        }
    }

    private static string ParleyHome()
    {
        var home = Environment.GetEnvironmentVariable("PARLEY_HOME");
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".parley")
            : Path.GetFullPath(home);
    }
}

public sealed record ClaudeEndpointRegistration(
    string RegistrationId,
    int ClaudePid,
    long ClaudeStartedAt,
    string EndpointSid,
    int ChannelServerPid,
    string RegisteredAt);

public sealed class ClaudeEndpointRegistrationHandle(
    ClaudeEndpointRegistry registry,
    ClaudeEndpointRegistration registration) : IDisposable
{
    public void Dispose() => registry.Remove(registration);
}
