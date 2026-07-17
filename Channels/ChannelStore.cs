using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ParleyCli.Models;

namespace ParleyCli.Channels;

/// <summary>
/// Filesystem-backed channel. Each channel is an append-only JSONL transcript under the parley
/// home dir (default <c>~/.parley</c>, overridable with <c>PARLEY_HOME</c>). Appends are
/// <b>lock-free</b>: each message is written as one atomic <c>O_APPEND</c> write, which the
/// kernel serializes under the file's inode lock, so concurrent writers never interleave and no
/// lock file is needed (this also sidesteps flock quirks inside sandboxes). Message <c>seq</c> is
/// its 1-based line position, derived on read. Each participant keeps its own read cursor keyed by
/// session id.
/// </summary>
public class ChannelStore
{
    // No '.' allowed: '.' is the field separator in cursor filenames ({channel}.{sid}.cursor),
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
    /// Appends a message as one atomic line and returns it with its assigned seq. Lock-free: the
    /// single O_APPEND write is serialized by the kernel. When <paramref name="expectNew"/> is set,
    /// asserts (best-effort — no lock) that the channel is still fresh (no sender has spoken twice
    /// and this session hasn't spoken) so an opener can catch a name collision.
    /// </summary>
    public Message Append(string channel, string from, string sid, string text, bool expectNew = false, bool closed = false)
    {
        Directory.CreateDirectory(ChannelsDir);

        if (expectNew)
        {
            var all = ReadAll(channel);
            var mine = all.Count(m => m.Sid == sid);
            var maxPerSender = all.GroupBy(m => m.Sid).Select(g => g.Count()).DefaultIfEmpty(0).Max();
            if (mine > 0 || maxPerSender > 1)
                throw new ArgumentException(
                    $"--expect-new: channel '{channel}' is not fresh ({all.Count} message(s) already present). " +
                    "The name likely collides with another conversation — use a channel with a fresh random suffix.");
        }

        var wire = new MessageWire(DateTimeOffset.UtcNow.ToString("o"), from, sid, text, closed ? true : null);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(wire, JsonOpts) + "\n");
        AtomicAppend(TranscriptPath(channel), bytes);

        // seq = my line position. Re-reading is exact for the common sequential case; under a
        // simultaneous append it may be off by one, which is harmless for coordination.
        var seq = ReadAll(channel).Count;
        return new Message(seq, wire.Ts, wire.From, wire.Sid, wire.Text, wire.Closed);
    }

    /// <summary>Returns the full transcript in order, assigning each message its 1-based seq (line position).</summary>
    public List<Message> ReadAll(string channel)
    {
        var path = TranscriptPath(channel);
        var result = new List<Message>();
        if (!File.Exists(path)) return result;

        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            MessageWire? wire;
            try { wire = JsonSerializer.Deserialize<MessageWire>(line, JsonOpts); }
            catch { continue; } // tolerate a torn/partial line rather than crashing a read
            if (wire == null) continue;
            result.Add(new Message(result.Count + 1, wire.Ts, wire.From, wire.Sid, wire.Text, wire.Closed));
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

    /// <summary>Channel names that have a transcript file (including ones emptied by <c>pop</c>).</summary>
    public List<string> ListChannels()
    {
        if (!Directory.Exists(ChannelsDir)) return new();
        return Directory.GetFiles(ChannelsDir, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }

    /// <summary>
    /// A channel's message count and last activity: the newest message's timestamp, or —
    /// for an emptied channel — the transcript file's last-write time.
    /// </summary>
    public (int count, DateTimeOffset lastActivity) ChannelActivity(string channel)
    {
        var all = ReadAll(channel);
        if (all.Count > 0 && DateTimeOffset.TryParse(all[^1].Ts, out var ts))
            return (all.Count, ts);

        var path = TranscriptPath(channel);
        var mtime = File.Exists(path)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
            : DateTimeOffset.MinValue;
        return (all.Count, mtime);
    }

    /// <summary>
    /// Removes the last message (highest seq) and rolls back any cursor that had read it, then
    /// returns the removed message. Guards against a concurrent append: throws if the current last
    /// seq isn't <paramref name="expectedLastSeq"/>. Rewrites via a temp file + atomic rename.
    /// For manual operator use (see the <c>admin</c> command).
    /// </summary>
    public Message Pop(string channel, int expectedLastSeq)
    {
        var all = ReadAll(channel);
        if (all.Count == 0)
            throw new ArgumentException($"Channel '{channel}' has no messages to pop.");

        var popped = all[^1];
        if (popped.Seq != expectedLastSeq)
            throw new ArgumentException(
                $"Channel '{channel}' changed since you looked (last is now #{popped.Seq}, expected #{expectedLastSeq}); aborted.");

        all.RemoveAt(all.Count - 1);
        var path = TranscriptPath(channel);
        var tmp = path + ".tmp";
        var body = all.Count == 0
            ? ""
            : string.Join("\n", all.Select(m =>
                  JsonSerializer.Serialize(new MessageWire(m.Ts, m.From, m.Sid, m.Text, m.Closed), JsonOpts))) + "\n";
        File.WriteAllText(tmp, body);
        File.Move(tmp, path, overwrite: true);

        ClampCursors(channel, all.Count); // seqs are contiguous 1..N, so the new max seq == remaining count
        return popped;
    }

    /// <summary>Deletes a channel's transcript and cursors. Used by <c>admin prune</c>.</summary>
    public void DeleteChannel(string channel)
    {
        if (!Directory.Exists(ChannelsDir)) return;
        SafeDelete(TranscriptPath(channel));
        SafeDelete(TranscriptPath(channel) + ".tmp");
        foreach (var f in Directory.GetFiles(ChannelsDir, $"{channel}.*.cursor"))
            SafeDelete(f);
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

    private static void SafeDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int NativeOpen(string pathname, int flags, int mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "write")]
    private static extern nint NativeWrite(int fd, byte[] buf, nuint count);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int NativeClose(int fd);

    /// <summary>
    /// Appends <paramref name="data"/> as a single atomic write with no lock. On Unix this uses
    /// POSIX <c>O_APPEND</c>: the kernel serializes each <c>write()</c> at the true end of file, so
    /// concurrent writers never lose or interleave data — and no <c>flock</c> is involved, which is
    /// what makes it work inside sandboxes that restrict flock. .NET's <c>FileMode.Append</c> can't
    /// be used: it seeks to EOF at open time, so concurrent appends overwrite each other.
    /// </summary>
    private static void AtomicAppend(string path, byte[] data)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // Windows: FILE_APPEND_DATA gives atomic append; FileMode.Append is fine there.
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            fs.Write(data, 0, data.Length);
            fs.Flush();
            return;
        }

        const int O_WRONLY = 0x1;
        var O_CREAT = OperatingSystem.IsMacOS() ? 0x0200 : 0x0040;
        var O_APPEND = OperatingSystem.IsMacOS() ? 0x0008 : 0x0400;

        var fd = NativeOpen(path, O_WRONLY | O_CREAT | O_APPEND, 0x1A4 /* 0644 */);
        if (fd < 0)
            throw new IOException($"open('{path}') failed (errno {Marshal.GetLastWin32Error()}).");
        try
        {
            // Single write() of the whole line: atomic append under O_APPEND.
            var written = NativeWrite(fd, data, (nuint)data.Length);
            if (written < 0)
                throw new IOException($"write('{path}') failed (errno {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            NativeClose(fd);
        }
    }
}
