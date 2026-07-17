using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ParleyCli.Models;

namespace ParleyCli.Channels;

/// <summary>
/// Filesystem-backed two-party channel. Each channel is an append-only JSONL
/// transcript under the parley home dir (default <c>~/.parley</c>, overridable
/// with the <c>PARLEY_HOME</c> env var). Writes serialize through a per-channel
/// advisory lock file; reads are lock-free (append-only ⇒ a reader sees a
/// consistent prefix). Each participant keeps its own read cursor.
/// </summary>
public class ChannelStore
{
    // No '.' allowed: '.' is the field separator in cursor filenames ({channel}.{id}.cursor),
    // so permitting it in either name would make that encoding ambiguous (distinct pairings
    // could map to one file) and would admit '.'/'..' path traversal.
    private static readonly Regex NameRe = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        // Only omit nulls (not every type-default), so a closing message writes "closed":true
        // and normal ones omit it — without silently dropping meaningful defaults on other fields.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;

    public ChannelStore()
    {
        var home = Environment.GetEnvironmentVariable("PARLEY_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".parley");
        _root = home;
    }

    public string ChannelsDir => Path.Combine(_root, "channels");

    private string TranscriptPath(string channel) => Path.Combine(ChannelsDir, $"{channel}.jsonl");
    private string LockPath(string channel) => Path.Combine(ChannelsDir, $"{channel}.lock");
    // Cursor is tagged with the sender's unique session id, so any number of sessions
    // can share a channel and each track its own read position independently.
    private string CursorPath(string channel, string sid) => Path.Combine(ChannelsDir, $"{channel}.{sid}.cursor");

    /// <summary>Validates a channel name or session id used verbatim in a filename.</summary>
    public static string Validate(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !NameRe.IsMatch(value) || value.Length > 100)
            throw new ArgumentException(
                $"Invalid {kind} '{value}': use only letters, digits, '_', '-' (no dots; max 100 chars).");
        return value;
    }

    /// <summary>
    /// Appends a message under the channel lock and returns it with its assigned seq.
    /// When <paramref name="expectNew"/> is set, first asserts (atomically, under the same
    /// lock) that the channel is still fresh — no sender has spoken twice and this session
    /// has not spoken at all — so an opener can detect that the channel name collided with
    /// an existing conversation. Throws <see cref="ArgumentException"/> if not fresh.
    /// </summary>
    public Message Append(string channel, string from, string sid, string text, bool expectNew = false, bool closed = false)
    {
        Directory.CreateDirectory(ChannelsDir);
        using var _ = AcquireLock(channel);

        var all = ReadAll(channel);
        if (expectNew)
        {
            var mine = all.Count(m => m.Sid == sid);
            var maxPerSender = all.GroupBy(m => m.Sid).Select(g => g.Count()).DefaultIfEmpty(0).Max();
            if (mine > 0 || maxPerSender > 1)
                throw new ArgumentException(
                    $"--expect-new: channel '{channel}' is not fresh ({all.Count} message(s) already present). " +
                    "The name likely collides with another conversation — use a channel with a fresh random suffix.");
        }

        var seq = all.Count + 1;
        var msg = new Message(seq, DateTimeOffset.UtcNow.ToString("o"), from, sid, text, closed ? true : null);
        File.AppendAllText(TranscriptPath(channel), JsonSerializer.Serialize(msg, JsonOpts) + "\n");
        return msg;
    }

    /// <summary>Returns the full transcript in seq order (empty if the channel has no messages yet).</summary>
    public List<Message> ReadAll(string channel)
    {
        var path = TranscriptPath(channel);
        var result = new List<Message>();
        if (!File.Exists(path)) return result;

        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var msg = JsonSerializer.Deserialize<Message>(line, JsonOpts);
            if (msg != null) result.Add(msg);
        }
        return result;
    }

    public int GetCursor(string channel, string sid)
    {
        var path = CursorPath(channel, sid);
        return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : 0;
    }

    public void SetCursor(string channel, string sid, int seq)
    {
        Directory.CreateDirectory(ChannelsDir);
        File.WriteAllText(CursorPath(channel, sid), seq.ToString());
    }

    /// <summary>
    /// Blocks until a message from another session (sid != <paramref name="mySid"/>) with
    /// seq &gt; <paramref name="afterSeq"/> exists, or the timeout elapses. Returns the full
    /// transcript snapshot that satisfied the wait (or the last snapshot on timeout, with
    /// <paramref name="satisfied"/> = false). Polls every 200ms.
    /// </summary>
    public async Task<(bool satisfied, List<Message> snapshot)> WaitForPeer(
        string channel, string mySid, int afterSeq, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var all = ReadAll(channel);
            if (all.Any(m => m.Sid != mySid && m.Seq > afterSeq))
                return (true, all);

            if (sw.Elapsed.TotalSeconds >= timeoutSeconds)
                return (false, all);

            await Task.Delay(200, ct);
        }
    }

    /// <summary>
    /// Removes the last message (highest seq) from a channel and rolls back any cursor that
    /// had already read it, then returns the removed message. Guards against a concurrent
    /// append: throws if the current last seq isn't <paramref name="expectedLastSeq"/>.
    /// For manual operator use (see the <c>admin</c> command).
    /// </summary>
    public Message Pop(string channel, int expectedLastSeq)
    {
        using var _ = AcquireLock(channel);

        var all = ReadAll(channel);
        if (all.Count == 0)
            throw new ArgumentException($"Channel '{channel}' has no messages to pop.");

        var popped = all[^1];
        if (popped.Seq != expectedLastSeq)
            throw new ArgumentException(
                $"Channel '{channel}' changed since you looked (last is now #{popped.Seq}, expected #{expectedLastSeq}); aborted.");

        all.RemoveAt(all.Count - 1);
        var path = TranscriptPath(channel);
        File.WriteAllText(path,
            all.Count == 0 ? "" : string.Join("\n", all.Select(m => JsonSerializer.Serialize(m, JsonOpts))) + "\n");

        ClampCursors(channel, all.Count); // seqs are contiguous 1..N, so the new max seq == remaining count
        return popped;
    }

    /// <summary>Clamps every cursor for a channel down to <paramref name="max"/> (after a message was removed).</summary>
    private void ClampCursors(string channel, int max)
    {
        if (!Directory.Exists(ChannelsDir)) return;
        foreach (var file in Directory.GetFiles(ChannelsDir, $"{channel}.*.cursor"))
        {
            if (int.TryParse(File.ReadAllText(file).Trim(), out var v) && v > max)
                File.WriteAllText(file, max.ToString());
        }
    }

    private FileStream AcquireLock(string channel)
    {
        var lockPath = LockPath(channel);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException($"Could not acquire lock for channel '{channel}' within 10s.");
                Thread.Sleep(50);
            }
        }
    }
}
