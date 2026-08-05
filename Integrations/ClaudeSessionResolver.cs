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
    ClaudeWakeClient wakeClient)
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

        var rebound = await wakeClient.RebindAsync(membership.Sid, currentSid, ct);
        if (rebound.Status != WakeStatus.Woken)
        {
            Log.Verbose("[trace] Claude SID repair endpoint rebind failed; oldSid={OldSid} currentSid={CurrentSid} status={Status}",
                membership.Sid, currentSid, rebound.Status);
            return false;
        }

        var rotated = store.RotateClaudeSession(
            membership.Sid, currentSid,
            new ClaudeProcessCorrelation(sameProcess.Pid, sameProcess.StartedAt));
        Log.Information("Rebound Claude session after lifecycle transition; oldSid={OldSid} newSid={NewSid} memberships={Memberships}",
            membership.Sid, currentSid, rotated);
        return rotated > 0;
    }
}

public readonly record struct ClaudeProcessCorrelation(int Pid, long StartedAt);
