namespace ParleyCli.Models;

/// <summary>
/// One line in a channel transcript. Serialized as a single JSON object per line
/// (JSONL); <see cref="Text"/> may contain embedded newlines (JSON-escaped).
/// </summary>
/// <param name="Seq">Monotonic 1-based position in the channel.</param>
/// <param name="Ts">ISO-8601 UTC timestamp the message was appended.</param>
/// <param name="From">Participant id that sent it.</param>
/// <param name="Text">Message body.</param>
public record Message(int Seq, string Ts, string From, string Text);
