namespace ParleyCli.Models;

/// <summary>
/// One line in a channel's roster log (<c>&lt;channel&gt;.roster.jsonl</c>, append-only). Each entry
/// is a role claim binding <see cref="Role"/> to the claiming session's <see cref="Sid"/>. The
/// current owner of a role is the latest entry for it; a plain claim is only written when the role
/// is free or already this sid's, while a <see cref="Forced"/> claim takes it over (session restart).
/// </summary>
public record RosterEntryWire(string Ts, string Role, string Sid, string? Wake = null, bool? Forced = null);

/// <summary>Resolved roster participant: the role, its current owning sid, and activity.</summary>
/// <param name="Role">The claimed role.</param>
/// <param name="Sid">Session id that currently owns the role.</param>
/// <param name="JoinedAt">Timestamp of the claim that established current ownership.</param>
/// <param name="MessageCount">How many messages this sid has sent to the channel.</param>
/// <param name="LastActivity">Timestamp of this sid's most recent message, or the join time if it hasn't spoken.</param>
public record Participant(string Role, string Sid, string Wake, string JoinedAt, int MessageCount, string LastActivity);
