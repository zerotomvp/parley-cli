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
    private static readonly Regex NameRe = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
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
    private string CursorPath(string channel, string id) => Path.Combine(ChannelsDir, $"{channel}.{id}.cursor");

    /// <summary>Validates a channel or participant id used verbatim in a filename.</summary>
    public static string Validate(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !NameRe.IsMatch(value) || value.Length > 100)
            throw new ArgumentException(
                $"Invalid {kind} '{value}': use only letters, digits, '.', '_', '-' (max 100 chars).");
        return value;
    }

    /// <summary>Appends a message under the channel lock and returns it with its assigned seq.</summary>
    public Message Append(string channel, string from, string text)
    {
        Directory.CreateDirectory(ChannelsDir);
        using var _ = AcquireLock(channel);

        var seq = ReadAll(channel).Count + 1;
        var msg = new Message(seq, DateTimeOffset.UtcNow.ToString("o"), from, text);
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

    public int GetCursor(string channel, string id)
    {
        var path = CursorPath(channel, id);
        return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : 0;
    }

    public void SetCursor(string channel, string id, int seq)
    {
        Directory.CreateDirectory(ChannelsDir);
        File.WriteAllText(CursorPath(channel, id), seq.ToString());
    }

    /// <summary>
    /// Blocks until a peer (from != <paramref name="me"/>) message with seq &gt;
    /// <paramref name="afterSeq"/> exists, or the timeout elapses. Returns the full
    /// transcript snapshot that satisfied the wait (or the last snapshot on timeout,
    /// with <paramref name="satisfied"/> = false). Polls every 200ms.
    /// </summary>
    public async Task<(bool satisfied, List<Message> snapshot)> WaitForPeer(
        string channel, string me, int afterSeq, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var all = ReadAll(channel);
            if (all.Any(m => m.From != me && m.Seq > afterSeq))
                return (true, all);

            if (sw.Elapsed.TotalSeconds >= timeoutSeconds)
                return (false, all);

            await Task.Delay(200, ct);
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
