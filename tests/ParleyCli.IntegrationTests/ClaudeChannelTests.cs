using System.Text.Json;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace ParleyCli.IntegrationTests;

public sealed class ClaudeChannelTests
{
    [Fact]
    public async Task Live_channel_is_detected_per_send_and_receives_wake_notice()
    {
        using var cli = new CliSandbox();
        var channel = cli.StartInteractive("integrations", "claude", "--sid", "recipient-sid");
        try
        {
            // A join probe only establishes that the endpoint exists. It must not race the MCP
            // initialization handshake, which Claude can complete after the CLI has already joined.
            (await cli.RunAsync("join", "claude-wake", "--as", "sender", "--sid", "sender-sid", "--wake", "never")).ShouldSucceed();
            var joined = await JoinWhenEndpointReadyAsync(
                cli, "claude-wake", "recipient", "recipient-sid");
            joined.ShouldSucceed();
            Assert.True(joined.Stderr.Contains("live Claude Code channel endpoint is available"),
                $"join did not detect the uninitialized channel. join stderr:\n{joined.Stderr}");
            Assert.Contains("Do not maintain a blocking recv listener", joined.Stderr);
            Assert.DoesNotContain("--wait", joined.Stderr);

            await channel.Process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"initialize","id":1,"params":{"protocolVersion":"2025-06-18"}}""");
            await channel.Process.StandardInput.FlushAsync();
            var initialized = await ReadLineAsync(channel.Process.StandardOutput);
            using (var response = JsonDocument.Parse(initialized))
            {
                Assert.Equal(1, response.RootElement.GetProperty("id").GetInt32());
                Assert.True(response.RootElement.GetProperty("result").GetProperty("capabilities")
                    .GetProperty("experimental").TryGetProperty("claude/channel", out _));
            }
            await channel.Process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""");
            await channel.Process.StandardInput.FlushAsync();
            await Task.Delay(100);

            var sent = await cli.RunAsync("send", "claude-wake", "--as", "sender", "--sid", "sender-sid",
                "--to", "recipient", "-m", "Please review this.");
            sent.ShouldSucceed();
            Assert.Contains("woke recipient through Claude Code channel", sent.Stderr);

            var notice = await ReadLineAsync(channel.Process.StandardOutput);
            using var notification = JsonDocument.Parse(notice);
            Assert.Equal("notifications/claude/channel",
                notification.RootElement.GetProperty("method").GetString());
            var content = notification.RootElement.GetProperty("params").GetProperty("content").GetString();
            Assert.Contains("[Parley #1 pending · claude-wake · recipient]", content);
            Assert.Contains("parley recv claude-wake --as recipient --last-seen", content);
            Assert.Contains("One foreground receive only—no --wait, &, or listener", content);
            Assert.Contains("highest message seq whose body was read; 0 if none", content);
            Assert.Contains("Do not pass 1 solely from this notice; use the prior checkpoint or 0", content);
            Assert.Contains("Replay is safe", content);

            var sentAgain = await cli.RunAsync("send", "claude-wake", "--as", "sender", "--sid", "sender-sid",
                "--to", "recipient", "-m", "One more request.");
            sentAgain.ShouldSucceed();
            Assert.Contains("woke recipient through Claude Code channel", sentAgain.Stderr);
            var secondNotice = await ReadLineAsync(channel.Process.StandardOutput);
            using var secondNotification = JsonDocument.Parse(secondNotice);
            Assert.Contains("[Parley #2 pending", secondNotification.RootElement
                .GetProperty("params").GetProperty("content").GetString());
        }
        finally
        {
            channel.Process.StandardInput.Close();
            await channel.Completion;
        }
    }

    [Fact]
    public async Task Disconnected_pipe_client_does_not_terminate_channel_server()
    {
        using var cli = new CliSandbox();
        var channel = cli.StartInteractive("integrations", "claude", "--sid", "resilient-sid");
        try
        {
            // A probe client can disappear before reading its acknowledgement.
            await SendAndAbandonAsync(cli.Store, "resilient-sid", "");
            await Task.Delay(300);

            await using (var abandoned = new NamedPipeClientStream(
                ".", PipeName(cli.Store, "resilient-sid"), PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await abandoned.ConnectAsync(timeout.Token);
            }
            await Task.Delay(300);

            await channel.Process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"initialize","id":1,"params":{"protocolVersion":"2025-06-18"}}""");
            await channel.Process.StandardInput.FlushAsync();
            _ = await ReadLineAsync(channel.Process.StandardOutput);
            await channel.Process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""");
            await channel.Process.StandardInput.FlushAsync();

            // Neither disconnected client is required to produce MCP output. The observable
            // contract is that the server accepts a later probe and initialized wake.
            var joined = await JoinWhenEndpointReadyAsync(
                cli, "resilient", "recipient", "resilient-sid");
            joined.ShouldSucceed();
            Assert.Contains("live Claude Code channel endpoint is available", joined.Stderr);

            (await cli.RunAsync("join", "resilient", "--as", "sender",
                "--sid", "sender-sid", "--wake", "never")).ShouldSucceed();
            var sent = await cli.RunAsync("send", "resilient", "--as", "sender",
                "--sid", "sender-sid", "--to", "recipient", "-m", "still alive");
            sent.ShouldSucceed();
            Assert.Contains("woke recipient", sent.Stderr);
            Assert.Contains("[Parley #1 pending", await ReadLineAsync(channel.Process.StandardOutput));
        }
        finally
        {
            channel.Process.StandardInput.Close();
            await channel.Completion;
        }
    }

    [Fact]
    public async Task Unexpected_pipe_frame_is_traced_and_followed_by_another_accept()
    {
        using var cli = new CliSandbox();
        var channel = cli.StartInteractiveWithEnvironment(
            new Dictionary<string, string> { ["PARLEY_TRACE"] = "1" },
            "integrations", "claude", "--sid", "fault-sid");
        var stderr = channel.Process.StandardError.ReadToEndAsync();
        try
        {
            await SendAndAbandonAsync(cli.Store, "fault-sid", "@parley/rebind:bad.sid");
            await Task.Delay(300);
            Assert.Equal("ok", await ProbeAsync(cli.Store, "fault-sid"));
        }
        finally
        {
            channel.Process.StandardInput.Close();
            await channel.Completion;
        }
        Assert.Contains("failed unexpectedly; another accept will be attempted", await stderr);
    }

    [Fact]
    public async Task Claude_clear_rotates_memberships_and_rebinds_the_live_endpoint()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        const string oldSid = "old-claude-sid";
        const string newSid = "new-claude-sid";
        cli.ConfigureClaudeAgents(AgentsJson(oldSid, pid: 4242, startedAt: 123456));

        var channel = cli.StartInteractive("integrations", "claude", "--sid", oldSid);
        try
        {
            await InitializeAsync(channel.Process);
            (await cli.RunAsync("join", "clear-one", "--as", "sender", "--sid", "sender-sid", "--wake", "never")).ShouldSucceed();
            (await cli.RunAsync("join", "clear-one", "--as", "recipient", "--sid", oldSid, "--wake", "claude")).ShouldSucceed();
            (await cli.RunAsync("join", "clear-two", "--as", "recipient", "--sid", oldSid, "--wake", "claude")).ShouldSucceed();

            (await cli.RunAsync("send", "clear-one", "--as", "sender", "--sid", "sender-sid",
                "--to", "recipient", "-m", "before clear")).ShouldSucceed();
            _ = await ReadLineAsync(channel.Process.StandardOutput);
            (await cli.RunAsync("recv", "clear-one", "--as", "recipient", "--sid", oldSid,
                "--last-seen", "0")).ShouldSucceed();

            cli.UpdateClaudeAgents(AgentsJson(newSid, pid: 4242, startedAt: 123456));
            var repaired = await cli.RunAsync("recv", "clear-one", "--as", "recipient", "--sid", newSid,
                "--last-seen", "1");
            repaired.ShouldSucceed();
            Assert.Contains("No new messages", repaired.Stderr);
            Assert.Equal("1", File.ReadAllText(cli.Cursor("clear-one", newSid)));

            var firstRoster = await cli.RunAsync("members", "list", "clear-one", "--json");
            var secondRoster = await cli.RunAsync("members", "list", "clear-two", "--json");
            Assert.Contains(newSid, firstRoster.Stdout);
            Assert.Contains(newSid, secondRoster.Stdout);
            Assert.DoesNotContain(oldSid, firstRoster.Stdout);

            var after = await cli.RunAsync("send", "clear-one", "--as", "sender", "--sid", "sender-sid",
                "--to", "recipient", "-m", "after clear");
            after.ShouldSucceed();
            Assert.Contains("woke recipient", after.Stderr);
            var notice = await ReadLineAsync(channel.Process.StandardOutput);
            Assert.Contains("[Parley #2 pending", notice);

            // The old endpoint remains a grace alias, covering a sender that resolved the roster
            // immediately before rotation while the new SID is already accepting wakes.
            Assert.Equal("ok", await ProbeAsync(cli.Store, oldSid));
            Assert.Equal("ok", await ProbeAsync(cli.Store, newSid));
        }
        finally
        {
            channel.Process.StandardInput.Close();
            await channel.Completion;
        }
    }

    [Fact]
    public async Task Claude_clear_before_first_join_recovers_endpoint_from_process_registration()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        const string oldSid = "pre-clear-sid";
        const string newSid = "post-clear-sid";
        const int claudePid = 4242;
        const long claudeStartedAt = 123456;
        cli.ConfigureClaudeAgents(AgentsJson(oldSid, claudePid, claudeStartedAt));

        var channel = cli.StartInteractive("integrations", "claude", "--sid", oldSid);
        var registration = cli.ClaudeEndpointRegistration(claudePid, claudeStartedAt);
        try
        {
            await InitializeAsync(channel.Process);
            await WaitForFileAsync(registration);

            // /clear changes Claude's public UUID before this role has ever joined a Parley
            // conversation. There is therefore no old roster membership to drive SID repair.
            cli.UpdateClaudeAgents(AgentsJson(newSid, claudePid, claudeStartedAt));
            (await cli.RunAsync("join", "post-clear-new-channel", "--as", "sender",
                "--sid", "sender-sid", "--wake", "never")).ShouldSucceed();
            var joined = await cli.RunAsync("join", "post-clear-new-channel", "--as", "recipient",
                "--sid", newSid, "--wake", "claude");
            joined.ShouldSucceed();
            Assert.Contains("live Claude Code channel endpoint is available", joined.Stderr);

            var sent = await cli.RunAsync("send", "post-clear-new-channel", "--as", "sender",
                "--sid", "sender-sid", "--to", "recipient", "-m", "after pre-join clear");
            sent.ShouldSucceed();
            Assert.Contains("woke recipient", sent.Stderr);
            var notice = await ReadLineAsync(channel.Process.StandardOutput);
            Assert.Contains("[Parley #1 pending", notice);
            Assert.Contains("post-clear-new-channel", notice);

            var registered = JsonDocument.Parse(await File.ReadAllTextAsync(registration));
            Assert.Equal(newSid, registered.RootElement.GetProperty("endpointSid").GetString());
        }
        finally
        {
            channel.Process.StandardInput.Close();
            await channel.Completion;
        }

        Assert.False(File.Exists(registration),
            "The channel server should remove only its own process registration on shutdown.");
    }

    [Fact]
    public async Task Claude_channel_startup_prunes_stale_and_malformed_registrations()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        const string sid = "live-sid";
        cli.ConfigureClaudeAgents(AgentsJson(sid, pid: 42, startedAt: 100));
        var stale = cli.ClaudeEndpointRegistration(pid: 41, startedAt: 99);
        var malformed = cli.ClaudeEndpointRegistration(pid: 40, startedAt: 98);
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        await File.WriteAllTextAsync(stale,
            """{"registrationId":"stale","claudePid":41,"claudeStartedAt":99,"endpointSid":"old","channelServerPid":1,"registeredAt":"old"}""");
        await File.WriteAllTextAsync(malformed, "not json");

        var channel = cli.StartInteractive("integrations", "claude", "--sid", sid);
        var current = cli.ClaudeEndpointRegistration(pid: 42, startedAt: 100);
        try
        {
            await InitializeAsync(channel.Process);
            await WaitForFileAsync(current);
            Assert.False(File.Exists(stale));
            Assert.False(File.Exists(malformed));
        }
        finally
        {
            channel.Process.StandardInput.Close();
            await channel.Completion;
        }
    }

    [Fact]
    public async Task Claude_sid_rotation_rejects_changed_process_identity()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        cli.ConfigureClaudeAgents(AgentsJson("old-sid", pid: 7, startedAt: 100));
        (await cli.RunAsync("join", "identity", "--as", "recipient", "--sid", "old-sid", "--wake", "claude")).ShouldSucceed();

        cli.UpdateClaudeAgents(AgentsJson("new-sid", pid: 7, startedAt: 101));
        var rejected = await cli.RunAsync("recv", "identity", "--as", "recipient", "--sid", "new-sid",
            "--last-seen", "0");
        Assert.Equal(1, rejected.ExitCode);
        Assert.Contains("held by a different session", rejected.Stderr);
    }

    [Fact]
    public async Task Claude_sid_rotation_falls_back_when_agent_discovery_is_unavailable()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        cli.ConfigureClaudeAgents(AgentsJson("old-sid", pid: 7, startedAt: 100));
        (await cli.RunAsync("join", "no-discovery", "--as", "recipient", "--sid", "old-sid", "--wake", "claude")).ShouldSucceed();

        cli.UpdateClaudeAgents("this Claude version does not support JSON discovery");
        var rejected = await cli.RunAsync("recv", "no-discovery", "--as", "recipient", "--sid", "new-sid",
            "--last-seen", "0");
        Assert.Equal(1, rejected.ExitCode);
        Assert.Contains("held by a different session", rejected.Stderr);
    }

    [Fact]
    public async Task Explicit_force_reclaim_refreshes_Claude_process_correlation()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        cli.ConfigureClaudeAgents(AgentsJson("old-sid", pid: 7, startedAt: 100));
        (await cli.RunAsync("join", "force-claude", "--as", "recipient", "--sid", "old-sid", "--wake", "claude")).ShouldSucceed();

        cli.UpdateClaudeAgents(AgentsJson("replacement-sid", pid: 8, startedAt: 200));
        var reclaimed = await cli.RunAsync("join", "force-claude", "--as", "recipient",
            "--sid", "replacement-sid", "--wake", "claude", "--force");
        reclaimed.ShouldSucceed();
        Assert.Contains("reclaimed role", reclaimed.Stderr);

        var who = await cli.RunAsync("members", "list", "force-claude", "--json");
        Assert.Contains("replacement-sid", who.Stdout);
    }

    private static async Task InitializeAsync(Process process)
    {
        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","method":"initialize","id":1,"params":{"protocolVersion":"2025-06-18"}}""");
        await process.StandardInput.FlushAsync();
        _ = await ReadLineAsync(process.StandardOutput);
        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""");
        await process.StandardInput.FlushAsync();
    }

    private static async Task<CliResult> JoinWhenEndpointReadyAsync(
        CliSandbox cli, string channel, string role, string sid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var joined = await cli.RunAsync(
                "join", channel, "--as", role, "--sid", sid, "--wake", "claude");
            joined.ShouldSucceed();
            if (joined.Stderr.Contains("live Claude Code channel endpoint is available"))
                return joined;

            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path))
            await Task.Delay(20, timeout.Token);
    }

    private static async Task<string?> ProbeAsync(string home, string sid)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", PipeName(home, sid), PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("");
        return await reader.ReadLineAsync(timeout.Token);
    }

    private static async Task SendAndAbandonAsync(string home, string sid, string frame)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", PipeName(home, sid), PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            { AutoFlush = true };
        await writer.WriteLineAsync(frame);
    }

    private static string AgentsJson(string sid, int pid, long startedAt) =>
        $$"""[{"pid":{{pid}},"startedAt":{{startedAt}},"sessionId":"{{sid}}"}]""";

    private static string PipeName(string home, string sid)
    {
        var identity = $"{Path.GetFullPath(home)}\n{sid}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"parley-claude-{hash[..24]}";
    }

    private static async Task<string> ReadLineAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await reader.ReadLineAsync(timeout.Token)
            ?? throw new EndOfStreamException("Claude channel closed before producing the expected MCP frame.");
    }
}
