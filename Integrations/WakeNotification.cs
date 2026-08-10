namespace ParleyCli.Integrations;

internal static class WakeNotification
{
    public static string Create(int seq, string channel, string role) =>
        $"[Parley #{seq} pending · {channel} · {role}] " +
        "One foreground receive only—no --wait, &, or listener: " +
        $"parley recv {channel} --as {role} --last-seen " +
        $"<highest message seq whose body was read; 0 if none>. " +
        $"Do not pass {seq} solely from this notice; use the prior checkpoint or 0. Replay is safe.";
}
