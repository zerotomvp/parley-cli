namespace ParleyCli.Integrations;

internal static class WakeNotification
{
    public static string Create(int seq, string channel, string role) =>
        $"[Parley #{seq} pending · {channel} · {role}] " +
        "One foreground receive only—no --wait, &, or listener: " +
        $"parley recv {channel} --as {role} --last-seen " +
        $"<highest Parley seq in context; 0 if none>. Notice does not mark #{seq} seen.";
}
