using ParleyCli.Channels;
using Serilog;

namespace ParleyCli.Integrations;

/// <summary>
/// Repairs Claude's externally visible session UUID after an in-process lifecycle transition such
/// as /clear. Process identity is correlation only; the current Claude UUID remains Parley's SID.
/// </summary>
public sealed class ClaudeSessionResolver(
    ChannelStore store,
    ClaudeAgentDiscovery discovery,
    ClaudeWakeClient wakeClient,
    ClaudeEndpointRegistry endpointRegistry)
{
    public async Task<ClaudeProcessCorrelation?> CaptureAsync(string sid, CancellationToken ct)
    {
        var agents = await discovery.ListAsync(ct);
        var agent = agents?.SingleOrDefault(a => a.SessionId == sid);
        return agent is null ? null : new(agent.Pid, agent.StartedAt);
    }

    public async Task<bool> TryRepairMembershipAsync(
        string channel, string role, string currentSid, CancellationToken ct)
    {
        var membership = store.MembershipOf(channel, role);
        if (membership is null || membership.Sid == currentSid || membership.Wake != "claude"
            || membership.ClaudePid is null || membership.ClaudeStartedAt is null)
            return false;

        var agents = await discovery.ListAsync(ct);
        var sameProcess = agents?.SingleOrDefault(a =>
            a.Pid == membership.ClaudePid.Value
            && a.StartedAt == membership.ClaudeStartedAt.Value);
        if (sameProcess?.SessionId != currentSid)
        {
            Log.Verbose("[trace] Claude SID repair rejected; role={Role} oldSid={OldSid} currentSid={CurrentSid} processMatched={ProcessMatched}",
                role, membership.Sid, currentSid, sameProcess is not null);
            return false;
        }

        var process = new ClaudeProcessCorrelation(sameProcess.Pid, sameProcess.StartedAt);
        return await RebindAndRotateAsync(membership.Sid, currentSid, process, ct) > 0;
    }

    /// <summary>
    /// Repairs every active membership proven to belong to the current Claude process. Unlike the
    /// ordinary action path, a global identity query has no channel/role with which to locate the
    /// previous UUID, so this bounded scan uses stored process correlation.
    /// </summary>
    public async Task<int> TryRepairAllMembershipsAsync(string currentSid, CancellationToken ct)
    {
        var process = await CaptureAsync(currentSid, ct);
        if (process is null) return 0;

        var rotated = 0;
        foreach (var oldSid in store.ClaudeSessionIdsForProcess(process.Value)
                     .Where(sid => sid != currentSid))
            rotated += await RebindAndRotateAsync(oldSid, currentSid, process.Value, ct);
        return rotated;
    }

    private async Task<int> RebindAndRotateAsync(
        string oldSid, string currentSid, ClaudeProcessCorrelation process, CancellationToken ct)
    {
        var rebound = await wakeClient.RebindAsync(oldSid, currentSid, ct);
        if (rebound.Status != WakeStatus.Woken)
        {
            Log.Verbose("[trace] Claude SID repair endpoint rebind failed; oldSid={OldSid} currentSid={CurrentSid} status={Status}",
                oldSid, currentSid, rebound.Status);
            return 0;
        }

        var registration = endpointRegistry.Find(process);
        if (registration?.EndpointSid == oldSid)
            endpointRegistry.UpdateEndpoint(registration, currentSid);
        var rotated = store.RotateClaudeSession(oldSid, currentSid, process);
        Log.Information("Rebound Claude session after lifecycle transition; oldSid={OldSid} newSid={NewSid} memberships={Memberships}",
            oldSid, currentSid, rotated);
        return rotated;
    }

    /// <summary>
    /// Ensures the current public Claude UUID reaches the already-running channel endpoint. The
    /// registration fallback is used only when direct SID probing fails, covering /clear before a
    /// first join where no conversation membership can reveal the endpoint's previous SID.
    /// </summary>
    public async Task<WakeResult> EnsureEndpointAsync(
        string currentSid, ClaudeProcessCorrelation? process, CancellationToken ct)
    {
        var direct = await wakeClient.ProbeAsync(currentSid, ct);
        if (direct.Status == WakeStatus.Woken || process is null) return direct;

        var registration = endpointRegistry.Find(process.Value);
        if (registration is null || registration.EndpointSid == currentSid) return direct;

        Log.Verbose("[trace] Claude endpoint recovery attempting registered SID; registeredSid={RegisteredSid} currentSid={CurrentSid} claudePid={ClaudePid} claudeStartedAt={ClaudeStartedAt}",
            registration.EndpointSid, currentSid, process.Value.Pid, process.Value.StartedAt);
        var rebound = await wakeClient.RebindAsync(registration.EndpointSid, currentSid, ct);
        if (rebound.Status == WakeStatus.Woken)
        {
            endpointRegistry.UpdateEndpoint(registration, currentSid);
            Log.Information("Rebound Claude endpoint before first channel join; oldSid={OldSid} newSid={NewSid}",
                registration.EndpointSid, currentSid);
            return rebound;
        }

        if (rebound.Status == WakeStatus.Unavailable)
            endpointRegistry.Remove(registration);
        return rebound;
    }
}

public readonly record struct ClaudeProcessCorrelation(int Pid, long StartedAt);
