namespace ParleyCli.Integrations;

internal static class WakeNotification
{
    public static string Create(int seq, string channel, string role) =>
        $"[Parley] Message #{seq} is waiting on channel {channel} for role {role}. " +
        "Run exactly one nonblocking receive in the foreground; do not add --wait, " +
        "do not append &, and do not start a persistent listener. " +
        $"Command: parley recv {channel} --as {role} --last-seen <highest Parley seq in your context, or 0>. " +
        $"This notice does not count as seeing #{seq}.";
}
