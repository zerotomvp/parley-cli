namespace ParleyCli.Models;

/// <summary>
/// One line in a channel transcript. Serialized as a single JSON object per line
/// (JSONL); <see cref="Text"/> may contain embedded newlines (JSON-escaped).
/// </summary>
/// <param name="Seq">Monotonic 1-based position in the channel.</param>
/// <param name="Ts">ISO-8601 UTC timestamp the message was appended.</param>
/// <param name="From">Human-readable display label of the sender (e.g. "claude").</param>
/// <param name="Sid">Sender's unique session id — the identity used to key cursors and filter "not me".</param>
/// <param name="Text">Message body.</param>
/// <param name="Closed">True if the sender marked this message final (no reply expected — end of exchange); null/absent otherwise.</param>
public record Message(int Seq, string Ts, string From, string Sid, string Text, bool? Closed = null);

/// <summary>
/// On-disk shape of a transcript line. Deliberately omits <c>Seq</c> — position (and thus seq) is
/// implicit in line order and derived on read, so lock-free concurrent appends can't collide on a
/// precomputed seq.
/// </summary>
public record MessageWire(string Ts, string From, string Sid, string Text, bool? Closed = null);
