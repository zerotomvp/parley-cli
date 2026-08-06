using System.Text.Json;
using System.Text.RegularExpressions;

namespace ParleyCli.IntegrationTests;

public sealed class BehaviorTests
{
    [Fact]
    public async Task Claude_diagnostics_are_emitted_only_when_explicitly_enabled()
    {
        using var cli = new CliSandbox();

        var normal = await cli.RunAsync("join", "trace-off", "--as", "recipient", "--sid", "normal-sid", "--wake", "claude");
        normal.ShouldSucceed();
        Assert.DoesNotContain("[trace]", normal.Stderr);
        var normalized = Regex.Replace(normal.Stderr, @"\s+", " ");
        Assert.Contains("keep this foreground listener running", normalized);
        Assert.Contains("Do not move the fallback listener into the background", normalized);

        var traced = await cli.RunWithEnvironmentAsync(
            new Dictionary<string, string> { ["PARLEY_TRACE"] = "1" },
            "join", "trace-on", "--as", "recipient", "--sid", "traced-sid", "--wake", "claude");
        traced.ShouldSucceed();
        Assert.Contains("[trace] diagnostics enabled by PARLEY_TRACE", traced.Stderr);
        Assert.Contains("kind=probe", traced.Stderr);
        Assert.Contains("classification=unavailable", traced.Stderr);
    }

    [Fact]
    public async Task Tracing_can_be_enabled_in_config_and_environment_presence_takes_precedence()
    {
        using var cli = new CliSandbox();
        Directory.CreateDirectory(Path.GetDirectoryName(cli.ConfigFile)!);
        await File.WriteAllTextAsync(cli.ConfigFile, """{"trace":true}""");

        var configured = await cli.RunAsync(
            "join", "trace-config", "--as", "recipient", "--sid", "config-sid", "--wake", "claude");
        configured.ShouldSucceed();
        Assert.Contains($"[trace] diagnostics enabled by {cli.ConfigFile}", configured.Stderr);
        Assert.Contains("kind=probe", configured.Stderr);

        var disabledByEnvironment = await cli.RunWithEnvironmentAsync(
            new Dictionary<string, string> { ["PARLEY_TRACE"] = "0" },
            "join", "trace-env-off", "--as", "recipient", "--sid", "env-off-sid", "--wake", "claude");
        disabledByEnvironment.ShouldSucceed();
        Assert.DoesNotContain("[trace]", disabledByEnvironment.Stderr);

        await File.WriteAllTextAsync(cli.ConfigFile, """{"trace":false}""");
        var enabledByEnvironment = await cli.RunWithEnvironmentAsync(
            new Dictionary<string, string> { ["PARLEY_TRACE"] = "yes" },
            "join", "trace-env-on", "--as", "recipient", "--sid", "env-on-sid", "--wake", "claude");
        enabledByEnvironment.ShouldSucceed();
        Assert.Contains("[trace] diagnostics enabled by PARLEY_TRACE", enabledByEnvironment.Stderr);
    }

    [Fact]
    public async Task Join_is_idempotent_rejects_collisions_and_supports_forced_reclaim()
    {
        using var cli = new CliSandbox();
        (await cli.RunAsync("join", "claims", "--as", "author", "--sid", "sid-a", "--wake", "never")).ShouldSucceed();

        var again = await cli.RunAsync("join", "claims", "--as", "author", "--sid", "sid-a", "--wake", "never");
        again.ShouldSucceed();
        Assert.Contains("already holds role", again.Stderr);

        var collision = await cli.RunAsync("join", "claims", "--as", "author", "--sid", "sid-b", "--wake", "never");
        Assert.Equal(1, collision.ExitCode);
        Assert.Contains("already held", collision.Stderr);

        var wrongHarness = await cli.RunAsync("join", "claims", "--as", "author", "--sid", "sid-b",
            "--wake", "codex", "--force");
        Assert.Equal(1, wrongHarness.ExitCode);
        Assert.Contains("permanently registered with --wake", wrongHarness.Stderr);

        (await Send(cli, "claims", "author", "sid-a", "--broadcast", "-m", "before restart")).ShouldSucceed();

        var reclaim = await cli.RunAsync("join", "claims", "--as", "author", "--sid", "sid-b", "--wake", "never", "--force");
        reclaim.ShouldSucceed();
        Assert.Contains("reclaimed role", reclaim.Stderr);
        Assert.Contains("--last-seen 1", reclaim.Stderr);

        var oldOwner = await cli.RunAsync("send", "claims", "--as", "author", "--sid", "sid-a",
            "--broadcast", "-m", "stale");
        Assert.Equal(1, oldOwner.ExitCode);
        Assert.Contains("held by a different session", oldOwner.Stderr);
    }

    [Fact]
    public async Task Join_detect_requires_a_supported_harness_environment()
    {
        using var cli = new CliSandbox();
        var result = await cli.RunAsync("join", "manual", "--as", "author", "--sid", "sid-a");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Pass --wake never", result.Stderr);
    }

    [Fact]
    public async Task Join_detect_persists_the_concrete_harness_type()
    {
        using var cli = new CliSandbox();
        var result = await cli.RunWithEnvironmentAsync(
            new Dictionary<string, string> { ["CODEX_THREAD_ID"] = "codex-sid" },
            "join", "detected", "--as", "author");
        result.ShouldSucceed();

        var participant = await cli.RunAsync("who", "detected", "--json");
        participant.ShouldSucceed();
        Assert.Equal("codex", JsonDocument.Parse(participant.Stdout).RootElement
            .GetProperty("wake").GetString());
    }

    [Fact]
    public async Task Direct_and_broadcast_delivery_respect_roles()
    {
        using var cli = new CliSandbox();
        await Join(cli, "delivery", ("author", "a"), ("reviewer", "r"), ("observer", "o"));
        (await Send(cli, "delivery", "author", "a", "--to", "reviewer", "-m", "private")).ShouldSucceed();
        (await Send(cli, "delivery", "author", "a", "--broadcast", "-m", "public")).ShouldSucceed();

        var reviewer = await Recv(cli, "delivery", "reviewer", "r", 0);
        reviewer.ShouldSucceed();
        Assert.Contains("private", reviewer.Stdout);
        Assert.Contains("public", reviewer.Stdout);

        var observer = await Recv(cli, "delivery", "observer", "o", 0);
        observer.ShouldSucceed();
        Assert.DoesNotContain("private", observer.Stdout);
        Assert.Contains("public", observer.Stdout);
    }

    [Fact]
    public async Task Receive_footer_follows_the_persisted_wake_mode()
    {
        using var cli = new CliSandbox();
        (await cli.RunAsync("join", "footer", "--as", "sender", "--sid", "sender-sid", "--wake", "never")).ShouldSucceed();
        (await cli.RunAsync("join", "footer", "--as", "manual", "--sid", "manual-sid", "--wake", "never")).ShouldSucceed();
        (await cli.RunAsync("join", "footer", "--as", "automatic", "--sid", "automatic-sid", "--wake", "claude")).ShouldSucceed();
        (await cli.RunAsync("send", "footer", "--as", "sender", "--sid", "sender-sid",
            "--broadcast", "-m", "footer test")).ShouldSucceed();

        var manual = await cli.RunAsync("recv", "footer", "--as", "manual", "--sid", "manual-sid", "--last-seen", "0");
        manual.ShouldSucceed();
        var manualStatus = Regex.Replace(manual.Stderr, @"\s+", " ");
        Assert.Contains("Checkpoint: 1 · foreground only: parley recv footer --as manual --last-seen 1 --wait", manualStatus);

        var automatic = await cli.RunAsync("recv", "footer", "--as", "automatic", "--sid", "automatic-sid", "--last-seen", "0");
        automatic.ShouldSucceed();
        var automaticStatus = Regex.Replace(automatic.Stderr, @"\s+", " ");
        Assert.Contains("Checkpoint: 1", automaticStatus);
        Assert.DoesNotContain("await wake", automaticStatus);
        Assert.DoesNotContain("--wait", automaticStatus);
        Assert.DoesNotContain("message(s) from other session(s)", automaticStatus);
    }

    [Fact]
    public async Task Last_seen_is_required_and_replays_behind_the_cli_cursor()
    {
        using var cli = new CliSandbox();
        await Join(cli, "checkpoint", ("author", "a"), ("reviewer", "r"));
        (await Send(cli, "checkpoint", "author", "a", "--to", "reviewer", "-m", "replay-me")).ShouldSucceed();

        var missing = await cli.RunAsync("recv", "checkpoint", "--as", "reviewer", "--sid", "r");
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("Required", missing.Stderr + missing.Stdout, StringComparison.OrdinalIgnoreCase);

        (await Recv(cli, "checkpoint", "reviewer", "r", 0)).ShouldSucceed();
        var replay = await Recv(cli, "checkpoint", "reviewer", "r", 0);
        replay.ShouldSucceed();
        Assert.Contains("replay-me", replay.Stdout);
        Assert.Contains("replaying from model checkpoint", replay.Stderr);
    }

    [Fact]
    public async Task Blocking_receive_wakes_and_bounded_wait_returns_two()
    {
        using var cli = new CliSandbox();
        await Join(cli, "waiting", ("author", "a"), ("reviewer", "r"));

        var timeout = await cli.RunAsync("recv", "waiting", "--as", "reviewer", "--sid", "r",
            "--last-seen", "0", "--wait", "--timeout", "1");
        Assert.Equal(2, timeout.ExitCode);

        var waiting = cli.Start("recv", "waiting", "--as", "reviewer", "--sid", "r",
            "--last-seen", "0", "--wait", "--timeout", "5");
        await Task.Delay(300);
        (await Send(cli, "waiting", "author", "a", "--to", "reviewer", "-m", "wake now")).ShouldSucceed();
        var received = await waiting.Completion;
        received.ShouldSucceed();
        Assert.Contains("wake now", received.Stdout);
    }

    [Fact]
    public async Task Send_wait_returns_the_peer_reply()
    {
        using var cli = new CliSandbox();
        await Join(cli, "sendwait", ("author", "a"), ("reviewer", "r"));

        var waiting = cli.Start("send", "sendwait", "--as", "author", "--sid", "a",
            "--to", "reviewer", "-m", "question", "--wait", "--timeout", "5");
        await WaitForTranscriptLines(cli.Transcript("sendwait"), 1);
        (await Send(cli, "sendwait", "reviewer", "r", "--to", "author", "-m", "answer")).ShouldSucceed();

        var result = await waiting.Completion;
        result.ShouldSucceed();
        Assert.StartsWith("1", result.Stdout.TrimStart());
        Assert.Contains("answer", result.Stdout);
    }

    [Fact]
    public async Task Acknowledgements_are_normal_addressed_messages_and_validate_flags()
    {
        using var cli = new CliSandbox();
        await Join(cli, "acks", ("author", "a"), ("reviewer", "r"));
        (await Send(cli, "acks", "author", "a", "--to", "reviewer", "-m", "request")).ShouldSucceed();

        var ack = await cli.RunAsync("send", "acks", "--as", "reviewer", "--sid", "r",
            "--ack", "1", "-m", "Working now");
        ack.ShouldSucceed();
        var received = await Recv(cli, "acks", "author", "a", 0);
        Assert.Contains("[ack #1] Working now", received.Stdout);

        var invalid = await cli.RunAsync("send", "acks", "--as", "reviewer", "--sid", "r",
            "--ack", "1", "--to", "author", "-m", "bad");
        Assert.Equal(1, invalid.ExitCode);
        Assert.Contains("cannot be combined", invalid.Stderr);
    }

    [Fact]
    public async Task Closed_drop_log_and_show_preserve_expected_state()
    {
        using var cli = new CliSandbox();
        await Join(cli, "inspect", ("author", "a"), ("reviewer", "r"));
        var longBody = new string('x', 210) + "\nsecond line";
        (await Send(cli, "inspect", "author", "a", "--to", "reviewer", "--close", "-m", longBody)).ShouldSucceed();

        var closed = await Recv(cli, "inspect", "reviewer", "r", 0);
        Assert.Contains("[closed]", closed.Stdout);
        Assert.Contains("marked the exchange closed", closed.Stderr);

        var log = await cli.RunAsync("log", "inspect");
        log.ShouldSucceed();
        Assert.Contains("truncated", log.Stdout);
        Assert.DoesNotContain("second line", log.Stdout);
        var show = await cli.RunAsync("show", "inspect", "1");
        show.ShouldSucceed();
        Assert.Contains("second line", show.Stdout);

        var drop = await cli.RunAsync("drop", "inspect", "--as", "author", "--sid", "a", "--yes");
        drop.ShouldSucceed();
        Assert.Equal("0", File.ReadAllText(cli.Cursor("inspect", "r")));
        Assert.Empty(File.ReadAllText(cli.Transcript("inspect")));
    }

    [Theory]
    [InlineData("bad.channel", "author", "sid")]
    [InlineData("channel", "bad.role", "sid")]
    [InlineData("channel", "author", "bad.sid")]
    public async Task Invalid_names_are_rejected(string channel, string role, string sid)
    {
        using var cli = new CliSandbox();
        var result = await cli.RunAsync("join", channel, "--as", role, "--sid", sid, "--wake", "never");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Invalid", result.Stderr);
    }

    [Fact]
    public async Task Concurrent_writers_produce_complete_parseable_transcript()
    {
        using var cli = new CliSandbox();
        const int count = 20;
        var roles = Enumerable.Range(0, count).Select(i => ($"role{i}", $"sid{i}")).ToArray();
        await Join(cli, "concurrent", roles);

        var sends = roles.Select((identity, i) => Send(cli, "concurrent", identity.Item1, identity.Item2,
            "--broadcast", "-m", $"message-{i}")).ToArray();
        var results = await Task.WhenAll(sends);
        foreach (var result in results) result.ShouldSucceed();

        var lines = await File.ReadAllLinesAsync(cli.Transcript("concurrent"));
        Assert.Equal(count, lines.Length);
        var bodies = lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("text").GetString()).ToHashSet();
        Assert.Equal(count, bodies.Count);
    }

    [Fact]
    public async Task Automatic_wake_falls_back_when_codex_is_unavailable()
    {
        using var cli = new CliSandbox();
        await Join(cli, "fallback", ("author", "a"), ("reviewer", "r"));
        var sent = await cli.RunAsync("send", "fallback", "--as", "author", "--sid", "a",
            "--to", "reviewer", "-m", "durable anyway");
        sent.ShouldSucceed();
        Assert.Contains("durable anyway", File.ReadAllText(cli.Transcript("fallback")));
    }

    [Fact]
    public async Task Json_output_is_machine_readable_and_uses_camel_case()
    {
        using var cli = new CliSandbox();
        await Join(cli, "json", ("author", "a"), ("reviewer", "r"));
        var sent = await cli.RunAsync("send", "json", "--as", "author", "--sid", "a",
            "--to", "reviewer", "--json", "-m", "json body");
        sent.ShouldSucceed();
        Assert.Equal(1, JsonDocument.Parse(sent.Stdout).RootElement.GetProperty("seq").GetInt32());

        var received = await cli.RunAsync("recv", "json", "--as", "reviewer", "--sid", "r",
            "--last-seen", "0", "--json");
        received.ShouldSucceed();
        var message = JsonDocument.Parse(received.Stdout).RootElement;
        Assert.Equal("author", message.GetProperty("from").GetString());
        Assert.Equal("json body", message.GetProperty("text").GetString());

        var who = await cli.RunAsync("who", "json", "--json");
        who.ShouldSucceed();
        foreach (var line in who.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(JsonDocument.Parse(line).RootElement.TryGetProperty("role", out _), line);
    }

    private static async Task Join(CliSandbox cli, string channel, params (string role, string sid)[] identities)
    {
        foreach (var (role, sid) in identities)
            (await cli.RunAsync("join", channel, "--as", role, "--sid", sid, "--wake", "never")).ShouldSucceed();
    }

    private static Task<CliResult> Send(CliSandbox cli, string channel, string role, string sid,
        params string[] arguments) => cli.RunAsync(["send", channel, "--as", role, "--sid", sid,
            .. arguments]);

    private static Task<CliResult> Recv(CliSandbox cli, string channel, string role, string sid, int lastSeen) =>
        cli.RunAsync("recv", channel, "--as", role, "--sid", sid, "--last-seen", lastSeen.ToString());

    private static async Task WaitForTranscriptLines(string path, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && File.ReadLines(path).Count() >= count) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Transcript did not reach {count} lines: {path}");
    }
}
