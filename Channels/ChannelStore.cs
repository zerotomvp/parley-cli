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
    // Append-only role-claim log, replayed to resolve who owns each role (latest claim wins).
    private string RosterPath(string channel) => Path.Combine(ChannelsDir, $"{channel}.roster.jsonl");
    // Cursor is tagged with the sender's unique session id, so any number of sessions
    // can share a channel and each track its own read position independently.
    private string CursorPath(string channel, string sid) => Path.Combine(ChannelsDir, $"{channel}.{sid}.cursor");

    /// <summary>Validates a channel name, role, or session id used verbatim in a filename or address.</summary>
    public static string Validate(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !NameRe.IsMatch(value) || value.Length > 100)
            throw new ArgumentException(
                $"Invalid {kind} '{value}': use only letters, digits, '_', '-' (no dots; max 100 chars).");
        return value;
    }

    // ── Roster (role claims) ────────────────────────────────────────────────────────────────

    /// <summary>Replays a channel's roster log in claim order (torn last line tolerated).</summary>
    private List<RosterEntryWire> ReadRoster(string channel)
    {
        var path = RosterPath(channel);
        var result = new List<RosterEntryWire>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            RosterEntryWire? e;
            try { e = JsonSerializer.Deserialize<RosterEntryWire>(line, JsonOpts); }
            catch { continue; }
            if (e != null) result.Add(e);
        }
        return result;
    }

    /// <summary>Current owner of each role: the sid of the latest claim for it (latest entry wins).</summary>
    private Dictionary<string, RosterEntryWire> Owners(string channel)
    {
        var owners = new Dictionary<string, RosterEntryWire>();
        foreach (var e in ReadRoster(channel)) owners[e.Role] = e; // later entries overwrite earlier
        return owners;
    }

    /// <summary>The sid that currently owns <paramref name="role"/> on the channel, or null if unclaimed.</summary>
    public string? OwnerOf(string channel, string role) =>
        Owners(channel).TryGetValue(role, out var e) ? e.Sid : null;

    public enum JoinResult { Joined, AlreadyYours, Reclaimed }

    /// <summary>
    /// Claims <paramref name="role"/> for <paramref name="sid"/>. If the role is already held by a
    /// different sid, a plain join is rejected; <paramref name="force"/> takes it over (for a session
    /// that restarted under a new sid). Idempotent if this sid already owns it. Best-effort under
    /// concurrency: after appending we re-read and, if we lost a simultaneous claim, report it.
    /// </summary>
    public JoinResult Join(string channel, string role, string sid, bool force = false)
    {
        Directory.CreateDirectory(ChannelsDir);
        var owner = OwnerOf(channel, role);
        if (owner == sid) return JoinResult.AlreadyYours;
        if (owner != null && !force)
            throw new ArgumentException(
                $"Role '{role}' on channel '{channel}' is already held by another session. " +
                "Pick a different role, or pass --force to take it over (e.g. after a restart).");

        var wire = new RosterEntryWire(DateTimeOffset.UtcNow.ToString("o"), role, sid, force ? true : null);
        AtomicAppend(RosterPath(channel), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(wire, JsonOpts) + "\n"));

        // Re-read: if a concurrent claim landed after ours, latest-wins may have handed the role away.
        var nowOwner = OwnerOf(channel, role);
        if (nowOwner != sid)
            throw new ArgumentException(
                $"Lost a concurrent claim on role '{role}' (now held by another session). Re-join or pick another role.");

        return owner == null ? JoinResult.Joined : JoinResult.Reclaimed;
    }

    /// <summary>
    /// Asserts that <paramref name="sid"/> may act as <paramref name="role"/>: the role must be
    /// owned by this sid. Throws an actionable error otherwise (not joined, or held by someone else).
    /// </summary>
    public void VerifyMembership(string channel, string role, string sid)
    {
        var owner = OwnerOf(channel, role);
        if (owner == null)
            throw new ArgumentException(
                $"Not joined: no one holds role '{role}' on '{channel}'. Run: parley join {channel} --as {role}");
        if (owner != sid)
            throw new ArgumentException(
                $"Role '{role}' on '{channel}' is held by a different session. " +
                "Use the role you joined as, or `parley join --force` to take it over.");
    }

    /// <summary>
    /// The channel's participants — one per claimed role — with each owner's message count and last
    /// activity (most recent message, or the join time if it hasn't spoken yet). Ordered by join time.
    /// </summary>
    public List<Participant> Participants(string channel)
    {
        var msgs = ReadAll(channel);
        return ReadRoster(channel)
            .GroupBy(e => e.Role)
            .Select(g => g.Last()) // current owner = latest claim for the role
            .Select(e =>
            {
                var mine = msgs.Where(m => m.Sid == e.Sid).ToList();
                var last = mine.Count > 0 ? mine[^1].Ts : e.Ts;
                return new Participant(e.Role, e.Sid, e.Ts, mine.Count, last);
            })
            .OrderBy(p => p.JoinedAt, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Appends a message as one atomic line and returns it with its assigned seq. Lock-free: the
    /// single O_APPEND write is serialized by the kernel. Delivery is explicit: pass either a
    /// non-empty <paramref name="to"/> (addressed) or <paramref name="broadcast"/> = true, never
    /// both and never neither. When <paramref name="expectNew"/> is set, asserts (best-effort — no
    /// lock) that the channel is still fresh (no sender has spoken twice and this session hasn't
    /// spoken) so an opener can catch a name collision.
    /// </summary>
    public Message Append(string channel, string from, string sid, string text,
        string[]? to, bool broadcast, bool expectNew = false, bool closed = false)
    {
        var hasTo = to is { Length: > 0 };
        if (hasTo == broadcast)
            throw new ArgumentException("Delivery must be exactly one of --to <roles> or --broadcast.");

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

        var wire = new MessageWire(DateTimeOffset.UtcNow.ToString("o"), from, sid, text,
            hasTo ? to : null, broadcast ? true : null, closed ? true : null);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(wire, JsonOpts) + "\n");
        AtomicAppend(TranscriptPath(channel), bytes);

        // seq = my line position. Re-reading is exact for the common sequential case; under a
        // simultaneous append it may be off by one, which is harmless for coordination.
        var seq = ReadAll(channel).Count;
        return new Message(seq, wire.Ts, wire.From, wire.Sid, wire.Text, wire.To, wire.Broadcast, wire.Closed);
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
            result.Add(new Message(result.Count + 1, wire.Ts, wire.From, wire.Sid, wire.Text,
                wire.To, wire.Broadcast, wire.Closed));
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
    /// Blocks until a message addressed to <paramref name="myRole"/> (broadcast or <c>to</c>-me),
    /// from another session (sid != <paramref name="mySid"/>), with seq &gt; <paramref name="afterSeq"/>
    /// exists, or the timeout elapses — so a session only wakes on traffic actually meant for it.
    /// When <paramref name="timeoutSeconds"/> is &lt;= 0 the wait is indefinite (only ends on a
    /// relevant message or cancellation, so <paramref name="satisfied"/> is always true). Returns the
    /// full transcript snapshot that satisfied the wait (or the last snapshot on timeout, with
    /// <paramref name="satisfied"/> = false). Polls every 200ms.
    /// </summary>
    public async Task<(bool satisfied, List<Message> snapshot)> WaitForPeer(
        string channel, string mySid, string myRole, int afterSeq, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var all = ReadAll(channel);
            if (all.Any(m => m.Sid != mySid && m.Seq > afterSeq && m.IsFor(myRole)))
                return (true, all);

            if (timeoutSeconds > 0 && sw.Elapsed.TotalSeconds >= timeoutSeconds)
                return (false, all);

            await Task.Delay(200, ct);
        }
    }

    /// <summary>Channel names that have a transcript file (including ones emptied by <c>pop</c>).</summary>
    public List<string> ListChannels()
    {
        if (!Directory.Exists(ChannelsDir)) return new();
        return Directory.GetFiles(ChannelsDir, "*.jsonl")
            .Where(f => !f.EndsWith(".roster.jsonl", StringComparison.Ordinal)) // roster files aren't channels
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
                  JsonSerializer.Serialize(
                      new MessageWire(m.Ts, m.From, m.Sid, m.Text, m.To?.ToArray(), m.Broadcast, m.Closed),
                      JsonOpts))) + "\n";
        File.WriteAllText(tmp, body);
        File.Move(tmp, path, overwrite: true);

        ClampCursors(channel, all.Count); // seqs are contiguous 1..N, so the new max seq == remaining count
        return popped;
    }

    /// <summary>Deletes a channel's transcript, roster, and cursors. Used by <c>admin prune</c>.</summary>
    public void DeleteChannel(string channel)
    {
        if (!Directory.Exists(ChannelsDir)) return;
        SafeDelete(TranscriptPath(channel));
        SafeDelete(TranscriptPath(channel) + ".tmp");
        SafeDelete(RosterPath(channel));
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

    // 2-arg open only. open(2) is variadic — `int open(const char*, int, ...)` — and the
    // creation `mode` is the variadic argument. On Apple arm64 variadic args pass on the
    // STACK, but a fixed-signature P/Invoke passes `mode` in a register (x2), so the kernel
    // reads an uninitialized stack slot and the file gets a RANDOM permission mode (observed:
    // 000/005/140/751 across fresh channels → later appends fail EACCES). Linux/x64 pass
    // variadic args in registers, so it only broke on arm64 macOS. We sidestep the trap by
    // never passing a mode: the file is created ahead of time via a managed API (see
    // AtomicAppend) and opened here without O_CREAT, so this call is not variadic.
    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int NativeOpen(string pathname, int flags);

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

        // Create the file (with a deterministic 0644) through the managed API *before* the
        // native open, so the append path never passes a creation mode to variadic open(2)
        // — the arm64-macOS trap documented on NativeOpen. Race-safe: CreateNew is an atomic
        // O_EXCL create, and losing the race just means another writer got there first.
        if (!File.Exists(path))
        {
            try { using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { } }
            catch (IOException) { /* another process created it concurrently — fine */ }
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                                         | UnixFileMode.GroupRead | UnixFileMode.OtherRead); // 0644
            }
            catch { /* best-effort; managed create already yields <= 0644 under a normal umask */ }
        }

        const int O_WRONLY = 0x1;
        var O_APPEND = OperatingSystem.IsMacOS() ? 0x0008 : 0x0400;

        // 2-arg open (no O_CREAT, no mode) → not variadic → ABI-correct on arm64 macOS too.
        // O_APPEND is what gives the atomic-append-at-true-EOF guarantee; O_CREAT was orthogonal.
        var fd = NativeOpen(path, O_WRONLY | O_APPEND);
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
