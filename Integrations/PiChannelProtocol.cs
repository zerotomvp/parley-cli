namespace ParleyCli.Integrations;

/// <summary>JSONL event emitted by <c>parley pi-channel</c> to the Pi extension.</summary>
public sealed record PiChannelEvent(
    string Type,
    string? Id = null,
    string? Sid = null,
    string? Version = null,
    string? Notification = null);

/// <summary>JSONL acknowledgement returned by the Pi extension after submitting a wake.</summary>
public sealed record PiChannelResponse(string Id, bool Success, string? Error = null);
