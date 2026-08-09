namespace ParleyCli.Models;

/// <summary>One active role held by the current session.</summary>
public sealed record SessionMembership(
    string Channel,
    string Role,
    string Wake,
    bool Owner);
