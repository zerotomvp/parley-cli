using System.Text.Json;
using System.Text.RegularExpressions;

namespace ParleyCli.IntegrationTests;

public sealed class MembershipLifecycleTests
{
    [Fact]
    public async Task First_joined_role_is_reported_as_channel_owner()
    {
        using var cli = new CliSandbox();
        (await Join(cli, "owner-list", "lead", "lead-sid")).ShouldSucceed();
        (await Join(cli, "owner-list", "worker", "worker-sid")).ShouldSucceed();

        var listed = await cli.RunAsync("members", "list", "owner-list", "--json");
        listed.ShouldSucceed();
        var rows = listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

        Assert.True(rows.Single(row => row.GetProperty("role").GetString() == "lead")
            .GetProperty("owner").GetBoolean());
        Assert.False(rows.Single(row => row.GetProperty("role").GetString() == "worker")
            .GetProperty("owner").GetBoolean());
        Assert.Contains("lead  ·  owner", (await cli.RunAsync(
            "members", "list", "owner-list")).Stdout);
    }

    [Fact]
    public async Task Leave_vacates_role_and_revokes_the_departed_session()
    {
        using var cli = new CliSandbox();
        (await Join(cli, "leave-flow", "lead", "lead-sid")).ShouldSucceed();
        (await Join(cli, "leave-flow", "worker", "worker-sid")).ShouldSucceed();

        var left = await cli.RunAsync(
            "leave", "leave-flow", "--as", "worker", "--sid", "worker-sid");
        left.ShouldSucceed();
        Assert.Contains("vacated role", left.Stderr);
        Assert.Contains("broadcast #1", left.Stderr);

        var notice = JsonDocument.Parse((await File.ReadAllLinesAsync(
            cli.Transcript("leave-flow"))).Single()).RootElement;
        Assert.Equal("worker", notice.GetProperty("from").GetString());
        Assert.Equal("worker-sid", notice.GetProperty("sid").GetString());
        Assert.Equal("worker left the channel.", notice.GetProperty("text").GetString());
        Assert.True(notice.GetProperty("broadcast").GetBoolean());

        var received = await cli.RunAsync(
            "recv", "leave-flow", "--as", "lead", "--sid", "lead-sid",
            "--last-seen", "0", "--json");
        received.ShouldSucceed();
        Assert.Contains("\"text\":\"worker left the channel.\"", received.Stdout);

        var listed = await cli.RunAsync("members", "list", "leave-flow", "--json");
        listed.ShouldSucceed();
        Assert.Contains("\"role\":\"lead\"", listed.Stdout);
        Assert.DoesNotContain("\"role\":\"worker\"", listed.Stdout);

        var staleSend = await cli.RunAsync(
            "send", "leave-flow", "--as", "worker", "--sid", "worker-sid",
            "--broadcast", "-m", "still here");
        Assert.Equal(1, staleSend.ExitCode);
        Assert.Contains("no one holds role 'worker'", staleSend.Stderr);

        var wrongWake = await cli.RunAsync(
            "join", "leave-flow", "--as", "worker", "--sid", "other-worker-sid",
            "--wake", "codex");
        Assert.Equal(1, wrongWake.ExitCode);
        Assert.Contains("permanently registered with --wake never",
            Regex.Replace(wrongWake.Stderr, @"\s+", " "));

        var rejoined = await Join(cli, "leave-flow", "worker", "new-worker-sid");
        rejoined.ShouldSucceed();
    }

    [Fact]
    public async Task Only_owner_can_remove_an_active_member()
    {
        using var cli = new CliSandbox();
        (await Join(cli, "remove-auth", "lead", "lead-sid")).ShouldSucceed();
        (await Join(cli, "remove-auth", "reviewer", "reviewer-sid")).ShouldSucceed();
        (await Join(cli, "remove-auth", "worker", "worker-sid")).ShouldSucceed();

        var denied = await cli.RunAsync(
            "members", "remove", "remove-auth", "worker",
            "--as", "reviewer", "--sid", "reviewer-sid");
        Assert.Equal(1, denied.ExitCode);
        Assert.Contains("Only channel owner role 'lead'", denied.Stderr);

        var removed = await cli.RunAsync(
            "members", "remove", "remove-auth", "worker",
            "--as", "lead", "--sid", "lead-sid");
        removed.ShouldSucceed();
        Assert.Contains("removed role", removed.Stderr);

        var staleRecv = await cli.RunAsync(
            "recv", "remove-auth", "--as", "worker", "--sid", "worker-sid",
            "--last-seen", "0");
        Assert.Equal(1, staleRecv.ExitCode);
        Assert.Contains("no one holds role 'worker'", staleRecv.Stderr);
    }

    [Fact]
    public async Task Owner_role_can_leave_but_cannot_be_removed()
    {
        using var cli = new CliSandbox();
        (await Join(cli, "owner-exit", "lead", "lead-sid")).ShouldSucceed();
        (await Join(cli, "owner-exit", "worker", "worker-sid")).ShouldSucceed();

        var removeOwner = await cli.RunAsync(
            "members", "remove", "owner-exit", "lead",
            "--as", "lead", "--sid", "lead-sid");
        Assert.Equal(1, removeOwner.ExitCode);
        Assert.Contains("cannot be removed", removeOwner.Stderr);

        var leave = await cli.RunAsync(
            "leave", "owner-exit", "--as", "lead", "--sid", "lead-sid");
        leave.ShouldSucceed();

        var ownerAbsent = await cli.RunAsync(
            "members", "remove", "owner-exit", "worker",
            "--as", "lead", "--sid", "lead-sid");
        Assert.Equal(1, ownerAbsent.ExitCode);
        Assert.Contains("no one holds role 'lead'", ownerAbsent.Stderr);

        var rejoined = await Join(cli, "owner-exit", "lead", "new-lead-sid");
        rejoined.ShouldSucceed();
        Assert.Contains("\"owner\":true", (await cli.RunAsync(
            "members", "list", "owner-exit", "--json")).Stdout);
    }

    [Fact]
    public async Task Stale_removal_tombstone_does_not_remove_a_newer_owner()
    {
        using var cli = new CliSandbox();
        (await Join(cli, "remove-race", "lead", "lead-sid")).ShouldSucceed();
        (await Join(cli, "remove-race", "worker", "old-worker-sid")).ShouldSucceed();
        (await cli.RunAsync(
            "join", "remove-race", "--as", "worker", "--sid", "new-worker-sid",
            "--wake", "never", "--force")).ShouldSucceed();

        await File.AppendAllTextAsync(cli.Roster("remove-race"),
            "{\"ts\":\"2026-08-09T00:00:00Z\",\"role\":\"worker\",\"sid\":\"old-worker-sid\"," +
            "\"kind\":\"remove\",\"byRole\":\"lead\",\"bySid\":\"lead-sid\"}\n");

        var listed = await cli.RunAsync("members", "list", "remove-race", "--json");
        listed.ShouldSucceed();
        Assert.Contains("\"role\":\"worker\"", listed.Stdout);
        Assert.Contains("\"sid\":\"new-worker-sid\"", listed.Stdout);
    }

    private static Task<CliResult> Join(
        CliSandbox cli, string channel, string role, string sid) =>
        cli.RunAsync("join", channel, "--as", role, "--sid", sid, "--wake", "never");
}
