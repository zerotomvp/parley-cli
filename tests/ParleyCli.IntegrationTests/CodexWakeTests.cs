using ParleyCli.Integrations;

namespace ParleyCli.IntegrationTests;

public sealed class CodexWakeTests
{
    [Fact]
    public async Task No_loaded_sid_match_leaves_delivery_durable_without_submitting()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        await JoinPeers(cli, "notloaded");
        await using var server = new FakeCodexServer(["someone-else"]);
        cli.ConfigureRunningCodex(server.SocketPath);

        var sent = await SendAuto(cli, "notloaded", "message");
        sent.ShouldSucceed();
        Assert.Empty(server.SubmittedMethods);
        Assert.Contains("live Codex app-server endpoint", sent.Stderr);
        Assert.Contains("reviewer is unavailable", sent.Stderr);
        Assert.Contains("parley recv notloaded --as reviewer", sent.Stderr);
        Assert.Contains("message", File.ReadAllText(cli.Transcript("notloaded")));
    }

    [Theory]
    [InlineData(null, "turn/start")]
    [InlineData("turn-123", "turn/steer")]
    public async Task Loaded_thread_is_started_or_steered(string? activeTurn, string expectedMethod)
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        await JoinPeers(cli, "loaded");
        var rollout = activeTurn is null ? null : WriteActiveRollout(cli, activeTurn);
        await using var server = new FakeCodexServer(["recipient-sid"], activeTurn, rollout);
        cli.ConfigureRunningCodex(server.SocketPath);

        var sent = await SendAuto(cli, "loaded", "wake me");
        sent.ShouldSucceed();
        Assert.True(server.ConnectionCount > 0,
            $"Fake server was not contacted. stderr: {sent.Stderr}");
        Assert.Null(server.LastError);
        await server.WaitForSubmissionsAsync(1);
        Assert.Equal(expectedMethod, server.SubmittedMethods.Single());
        Assert.Contains("[Parley #", server.SubmittedPayloads.Single());
        if (activeTurn is not null)
            Assert.Contains($"\"expectedTurnId\":\"{activeTurn}\"", server.SubmittedPayloads.Single());
        Assert.Contains("woke reviewer", sent.Stderr);
        Assert.Single(server.ReadPayloads);
        Assert.Contains("\"includeTurns\":false", server.ReadPayloads.Single());
    }

    [Fact]
    public async Task Active_thread_falls_back_to_full_history_when_rollout_tail_is_unavailable()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        await JoinPeers(cli, "fallback");
        await using var server = new FakeCodexServer(["recipient-sid"], "turn-456");
        cli.ConfigureRunningCodex(server.SocketPath);

        var sent = await SendAuto(cli, "fallback", "fallback wake");
        sent.ShouldSucceed();
        await server.WaitForSubmissionsAsync(1);
        Assert.Equal("turn/steer", server.SubmittedMethods.Single());
        Assert.Equal(2, server.ReadPayloads.Count);
        Assert.Contains("\"includeTurns\":false", server.ReadPayloads[0]);
        Assert.Contains("\"includeTurns\":true", server.ReadPayloads[1]);
    }

    [Fact]
    public void Rollout_tail_requires_the_latest_lifecycle_event_to_be_an_active_turn()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,
            [
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-1\"}}",
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-1\"}}"
            ]);
            Assert.Null(CodexWakeClient.FindActiveTurnInRolloutTail(path));

            File.AppendAllText(path,
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-2\"}}\n");
            Assert.Equal("turn-2", CodexWakeClient.FindActiveTurnInRolloutTail(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task State_change_error_is_retried_once()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        await JoinPeers(cli, "retry");
        await using var server = new FakeCodexServer(["recipient-sid"], failSubmissions: 1);
        cli.ConfigureRunningCodex(server.SocketPath);

        var sent = await SendAuto(cli, "retry", "retry wake");
        sent.ShouldSucceed();
        await server.WaitForSubmissionsAsync(2);
        Assert.Equal(2, server.SubmittedMethods.Count);
        Assert.Contains("woke reviewer", sent.Stderr);
    }

    [Fact]
    public async Task Wake_failure_after_match_is_nonfatal_and_message_remains_durable()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        await JoinPeers(cli, "wakefail");
        await using var server = new FakeCodexServer(["recipient-sid"], failSubmissions: 2);
        cli.ConfigureRunningCodex(server.SocketPath);

        var sent = await SendAuto(cli, "wakefail", "safe message");
        sent.ShouldSucceed();
        Assert.Contains("message remains delivered", sent.Stderr);
        Assert.Contains("safe message", File.ReadAllText(cli.Transcript("wakefail")));
    }

    [Fact]
    public async Task Malformed_app_server_response_is_nonfatal_and_message_remains_durable()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        await JoinPeers(cli, "malformed");
        await using var server = new FakeCodexServer(["recipient-sid"],
            malformedAtMethod: "thread/loaded/list");
        cli.ConfigureRunningCodex(server.SocketPath);

        var sent = await SendAuto(cli, "malformed", "durable malformed");
        sent.ShouldSucceed();
        Assert.Contains("message remains delivered", sent.Stderr);
        Assert.Contains("durable malformed", File.ReadAllText(cli.Transcript("malformed")));
    }

    private static async Task JoinPeers(CliSandbox cli, string channel)
    {
        (await cli.RunAsync("join", channel, "--as", "author", "--sid", "sender-sid", "--wake", "never")).ShouldSucceed();
        (await cli.RunAsync("join", channel, "--as", "reviewer", "--sid", "recipient-sid", "--wake", "codex")).ShouldSucceed();
    }

    private static Task<CliResult> SendAuto(CliSandbox cli, string channel, string message) =>
        cli.RunAsync("send", channel, "--as", "author", "--sid", "sender-sid",
            "--to", "reviewer", "-m", message);

    private static string WriteActiveRollout(CliSandbox cli, string turnId)
    {
        var path = Path.Combine(cli.Store, $"rollout-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path,
            $"{{\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{turnId}\"}}}}\n");
        return path;
    }
}
