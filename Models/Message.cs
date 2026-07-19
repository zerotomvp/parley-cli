namespace ParleyCli.Models;

/// <summary>
/// One line in a channel transcript. Serialized as a single JSON object per line
/// (JSONL); <see cref="Text"/> may contain embedded newlines (JSON-escaped).
/// </summary>
/// <param name="Seq">Monotonic 1-based position in the channel.</param>
/// <param name="Ts">ISO-8601 UTC timestamp the message was appended.</param>
/// <param name="From">Sender's role — the addressable identity that claimed a slot via <c>join</c>.</param>
/// <param name="Sid">Sender's unique session id — keys cursors, proves role ownership, filters "not me".</param>
/// <param name="Text">Message body.</param>
/// <param name="To">Explicit recipient roles. Mutually exclusive with <see cref="Broadcast"/>.</param>
/// <param name="Broadcast">True if the message goes to every role. Mutually exclusive with <see cref="To"/>.</param>
/// <param name="Closed">True if the sender marked this message final (no reply expected — end of exchange); null/absent otherwise.</param>
public record Message(
    int Seq, string Ts, string From, string Sid, string Text,
    IReadOnlyList<string>? To = null, bool? Broadcast = null, bool? Closed = null)
{
    /// <summary>
    /// Whether this message is delivered to <paramref name="role"/>: a broadcast reaches everyone,
    /// otherwise the role must be listed in <see cref="To"/>. A message with neither field set (only
    /// possible for pre-addressing legacy lines, which the one-time migration rewrites to broadcast)
    /// is treated as broadcast so it is never silently hidden.
    /// </summary>
    public bool IsFor(string role) =>
        Broadcast == true
        || (To is { Count: > 0 } to && to.Contains(role))
        || (Broadcast is null && (To is null || To.Count == 0));
}

/// <summary>
/// On-disk shape of a transcript line. Deliberately omits <c>Seq</c> — position (and thus seq) is
/// implicit in line order and derived on read, so lock-free concurrent appends can't collide on a
/// precomputed seq. Exactly one of <see cref="To"/> / <see cref="Broadcast"/> is written (enforced
/// on the send path); the other is null and omitted.
/// </summary>
public record MessageWire(
    string Ts, string From, string Sid, string Text,
    string[]? To = null, bool? Broadcast = null, bool? Closed = null);
