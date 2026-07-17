namespace ParleyCli.Channels;

/// <summary>Who held a channel lock — recorded in the sidecar owner file for diagnostics.</summary>
/// <param name="Pid">Process id of the holder.</param>
/// <param name="Op">Operation it was running (append / pop / prune).</param>
/// <param name="Since">ISO-8601 UTC time it acquired the lock.</param>
public sealed record LockOwner(int Pid, string Op, string Since);

/// <summary>
/// Thrown when a channel's write lock can't be acquired within the timeout. Carries the
/// recorded holder (if any) so the operation wasn't silently anonymous, and is treated as a
/// retryable "busy" outcome (not a hard error) by the command layer.
/// </summary>
public sealed class ChannelLockException : Exception
{
    public string Channel { get; }
    public LockOwner? Holder { get; }

    public ChannelLockException(string channel, LockOwner? holder, string message) : base(message)
    {
        Channel = channel;
        Holder = holder;
    }
}
