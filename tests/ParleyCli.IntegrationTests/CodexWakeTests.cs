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
        await using var server = new FakeCodexServer(["recipient-sid"], activeTurn);
        cli.ConfigureRunningCodex(server.SocketPath);

        var sent = await SendAuto(cli, "loaded", "wake me");
        sent.ShouldSucceed();
        Assert.True(server.ConnectionCount > 0,
            $"Fake server was not contacted. stderr: {sent.Stderr}");
        Assert.Null(server.LastError);
        await server.WaitForSubmissionsAsync(1);
        Assert.Equal(expectedMethod, server.SubmittedMethods.Single());
        Assert.Contains("Parley notification", server.SubmittedPayloads.Single());
        if (activeTurn is not null)
            Assert.Contains($"\"expectedTurnId\":\"{activeTurn}\"", server.SubmittedPayloads.Single());
        Assert.Contains("woke reviewer", sent.Stderr);
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
        (await cli.RunAsync("join", channel, "--as", "author", "--sid", "sender-sid")).ShouldSucceed();
        (await cli.RunAsync("join", channel, "--as", "reviewer", "--sid", "recipient-sid")).ShouldSucceed();
    }

    private static Task<CliResult> SendAuto(CliSandbox cli, string channel, string message) =>
        cli.RunAsync("send", channel, "--as", "author", "--sid", "sender-sid",
            "--to", "reviewer", "-m", message);
}
