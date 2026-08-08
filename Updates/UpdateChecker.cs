using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ParleyCli.Logging;
using Serilog;

namespace ParleyCli.Updates;

internal sealed class UpdateChecker
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/zerotomvp/parley-cli/releases/latest";

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly DateTimeOffset _now;
    private readonly SemanticVersion _currentVersion;
    private readonly Func<string?> _upgradeCommand;

    internal UpdateChecker(
        HttpClient http,
        string cachePath,
        DateTimeOffset now,
        SemanticVersion currentVersion,
        Func<string?> upgradeCommand)
    {
        _http = http;
        _cachePath = cachePath;
        _now = now;
        _currentVersion = currentVersion;
        _upgradeCommand = upgradeCommand;
    }

    internal static async Task CheckAndNotifyAsync(string? command, TextWriter stderr)
    {
        if (command is not ("join" or "claude-channel" or "pi-channel") || !LoggingConfiguration.UpdateChecksEnabled)
            return;

        var assemblyVersion = ParleyVersion.Numeric;
        if (assemblyVersion is null)
            return;

        using var http = new HttpClient { Timeout = RequestTimeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "parley-cli", assemblyVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        var checker = new UpdateChecker(
            http,
            Path.Combine(LoggingConfiguration.ApplicationDirectory, "update-check.json"),
            DateTimeOffset.UtcNow,
            new SemanticVersion(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build),
            InstallationDetector.CurrentUpgradeCommand);

        try
        {
            await checker.RunAsync(stderr);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Verbose(ex, "[trace] update check failed");
        }
    }

    internal async Task RunAsync(TextWriter stderr)
    {
        await using var lease = TryAcquireLease();
        if (lease is null)
        {
            Log.Verbose("[trace] update check skipped because another process holds the lease");
            return;
        }

        var cache = await ReadCacheAsync();
        if (cache?.CheckedAt is not { } checkedAt || _now - checkedAt >= CheckInterval)
            cache = await RefreshAsync(cache);

        if (cache?.LatestVersion is not { } latestText
            || !SemanticVersion.TryParse(latestText, out var latest)
            || latest.CompareTo(_currentVersion) <= 0
            || string.Equals(cache.NotifiedVersion, latestText, StringComparison.Ordinal))
            return;

        await stderr.WriteLineAsync(
            $"Update available: Parley {_currentVersion} → {latest}");
        if (_upgradeCommand() is { } command)
            await stderr.WriteLineAsync($"Upgrade with: {command}");
        else if (cache.ReleaseUrl is { Length: > 0 } releaseUrl)
            await stderr.WriteLineAsync(releaseUrl);

        cache.NotifiedVersion = latestText;
        await WriteCacheAsync(cache);
    }

    private FileStream? TryAcquireLease()
    {
        var directory = Path.GetDirectoryName(_cachePath)!;
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(
                _cachePath + ".lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<UpdateCache?> RefreshAsync(UpdateCache? cache)
    {
        cache ??= new UpdateCache();
        cache.CheckedAt = _now;

        try
        {
            using var response = await _http.GetAsync(LatestReleaseUrl);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream);
                if (release?.TagName is { } tag
                    && SemanticVersion.TryParse(tag, out var latest))
                {
                    cache.LatestVersion = latest.ToString();
                    cache.ReleaseUrl = release.HtmlUrl;
                    Log.Verbose("[trace] update check completed; latestVersion={LatestVersion}", latest);
                }
            }
            else
            {
                Log.Verbose("[trace] update check returned HTTP {StatusCode}",
                    (int)response.StatusCode);
            }
        }
        finally
        {
            await WriteCacheAsync(cache);
        }

        return cache;
    }

    private async Task<UpdateCache?> ReadCacheAsync()
    {
        if (!File.Exists(_cachePath))
            return null;

        try
        {
            await using var stream = File.OpenRead(_cachePath);
            return await JsonSerializer.DeserializeAsync<UpdateCache>(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Verbose(ex, "[trace] update cache is unreadable; path={Path}", _cachePath);
            return null;
        }
    }

    private async Task WriteCacheAsync(UpdateCache cache)
    {
        var directory = Path.GetDirectoryName(_cachePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(_cachePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, cache);
            File.Move(temporary, _cachePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    internal readonly record struct SemanticVersion(int Major, int Minor, int Patch)
        : IComparable<SemanticVersion>
    {
        public int CompareTo(SemanticVersion other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0) return major;
            var minor = Minor.CompareTo(other.Minor);
            return minor != 0 ? minor : Patch.CompareTo(other.Patch);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";

        public static bool TryParse(string value, out SemanticVersion version)
        {
            version = default;
            var text = value.StartsWith('v') ? value[1..] : value;
            var parts = text.Split('.');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out var major)
                || !int.TryParse(parts[1], out var minor)
                || !int.TryParse(parts[2], out var patch)
                || major < 0 || minor < 0 || patch < 0)
                return false;

            version = new SemanticVersion(major, minor, patch);
            return true;
        }
    }

    internal sealed class UpdateCache
    {
        [JsonPropertyName("checkedAt")]
        public DateTimeOffset? CheckedAt { get; set; }

        [JsonPropertyName("latestVersion")]
        public string? LatestVersion { get; set; }

        [JsonPropertyName("releaseUrl")]
        public string? ReleaseUrl { get; set; }

        [JsonPropertyName("notifiedVersion")]
        public string? NotifiedVersion { get; set; }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }
    }
}
