namespace ParleyCli.IntegrationTests;

using System.Text.Json;

public sealed class WaitForJoinTests
{
    [Fact]
    public async Task Already_joined_role_returns_immediately_without_creating_cursor()
    {
        using var cli = new CliSandbox();
        (await Join(cli, "ready", "reviewer", "reviewer-sid")).ShouldSucceed();

        var result = await cli.RunAsync("members", "wait", "ready", "reviewer", "--timeout", "2");
        result.ShouldSucceed();
        Assert.Contains("reviewer\tsid=reviewer-sid\twake=never", result.Stdout);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(cli.Transcript("ready"))!, "*.cursor"));
    }

    [Fact]
    public async Task Delayed_join_satisfies_wait_for_multiple_roles()
    {
        using var cli = new CliSandbox();
        var waiting = cli.Start("members", "wait", "delayed", "author", "reviewer", "--timeout", "5");
        await Task.Delay(300);
        (await Join(cli, "delayed", "author", "author-sid")).ShouldSucceed();
        await Task.Delay(300);
        (await Join(cli, "delayed", "reviewer", "reviewer-sid")).ShouldSucceed();

        var result = await waiting.Completion;
        result.ShouldSucceed();
        Assert.Contains("author\tsid=author-sid", result.Stdout);
        Assert.Contains("reviewer\tsid=reviewer-sid", result.Stdout);
    }

    [Fact]
    public async Task Bounded_wait_returns_two_when_role_is_absent()
    {
        using var cli = new CliSandbox();
        var result = await cli.RunAsync("members", "wait", "missing", "reviewer", "--timeout", "1");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Roles not joined within 1s", result.Stderr);
    }

    [Fact]
    public async Task Wait_reports_latest_owner_after_force_reclaim()
    {
        using var cli = new CliSandbox();
        (await Join(cli, "reclaim-wait", "author", "old-sid")).ShouldSucceed();
        var waiting = cli.Start("members", "wait", "reclaim-wait", "author", "reviewer", "--timeout", "5");
        await Task.Delay(300);
        (await cli.RunAsync("join", "reclaim-wait", "--as", "author", "--sid", "new-sid",
            "--wake", "never", "--force")).ShouldSucceed();
        (await Join(cli, "reclaim-wait", "reviewer", "reviewer-sid")).ShouldSucceed();

        var result = await waiting.Completion;
        result.ShouldSucceed();
        Assert.Contains("author\tsid=new-sid", result.Stdout);
        Assert.DoesNotContain("author\tsid=old-sid", result.Stdout);
    }

    [Fact]
    public async Task Concurrent_plain_claims_yield_one_owner_to_waiter()
    {
        using var cli = new CliSandbox();
        var waiting = cli.Start("members", "wait", "claim-race", "reviewer", "--timeout", "5");
        var claims = await Task.WhenAll(
            Join(cli, "claim-race", "reviewer", "sid-a"),
            Join(cli, "claim-race", "reviewer", "sid-b"));
        Assert.Contains(claims, result => result.ExitCode == 0);

        var result = await waiting.Completion;
        result.ShouldSucceed();
        Assert.Matches(@"reviewer\tsid=sid-[ab]\twake=never", result.Stdout);

        // The waiter returns as soon as either atomic claim is visible. A second concurrent claim
        // may become authoritative immediately afterward, so its snapshot need not equal a later
        // `members list`; both must still resolve exactly one valid owner.
        var who = await cli.RunAsync("members", "list", "claim-race", "--json");
        who.ShouldSucceed();
        var currentSid = JsonDocument.Parse(who.Stdout).RootElement.GetProperty("sid").GetString();
        Assert.Contains(currentSid, new[] { "sid-a", "sid-b" });
    }

    private static Task<CliResult> Join(CliSandbox cli, string channel, string role, string sid) =>
        cli.RunAsync("join", channel, "--as", role, "--sid", sid, "--wake", "never");
}
