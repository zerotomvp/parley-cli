using System.Diagnostics;
using System.Text.Json;

namespace ParleyCli.IntegrationTests;

public sealed class WhoamiTests
{
    [Fact]
    public async Task Lists_only_active_roles_for_exact_sid_in_roster_only_channels()
    {
        using var cli = new CliSandbox();
        (await Join(cli, "zeta", "reviewer", "current-sid")).ShouldSucceed();
        (await Join(cli, "alpha", "lead", "current-sid")).ShouldSucceed();
        (await Join(cli, "alpha", "worker", "other-sid")).ShouldSucceed();
        (await Join(cli, "departed", "former", "current-sid")).ShouldSucceed();
        (await cli.RunAsync(
            "leave", "departed", "--as", "former", "--sid", "current-sid")).ShouldSucceed();
        (await Join(cli, "superseded", "reviewer", "current-sid")).ShouldSucceed();
        (await cli.RunAsync(
            "join", "superseded", "--as", "reviewer", "--sid", "replacement-sid",
            "--wake", "never", "--force")).ShouldSucceed();

        Assert.False(File.Exists(cli.Transcript("alpha")),
            "whoami must discover channels from roster files, not transcript files");

        var result = await cli.RunAsync("whoami", "--sid", "current-sid");
        result.ShouldSucceed();
        Assert.Equal(
            "alpha  ·  lead  ·  owner  ·  wake never\n" +
            "zeta  ·  reviewer  ·  owner  ·  wake never\n",
            result.Stdout.Replace("\r\n", "\n"));
        Assert.DoesNotContain("worker", result.Stdout);
        Assert.DoesNotContain("departed", result.Stdout);
        Assert.DoesNotContain("superseded", result.Stdout);
    }

    [Fact]
    public async Task Json_output_is_sorted_jsonl_and_contains_no_sid()
    {
        using var cli = new CliSandbox();
        (await Join(cli, "same-channel", "z-role", "shared-sid")).ShouldSucceed();
        (await Join(cli, "same-channel", "a-role", "shared-sid")).ShouldSucceed();

        var result = await cli.RunAsync("whoami", "--sid", "shared-sid", "--json");
        result.ShouldSucceed();
        var rows = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

        Assert.Equal(new[] { "a-role", "z-role" },
            rows.Select(row => row.GetProperty("role").GetString()));
        Assert.All(rows, row => Assert.False(row.TryGetProperty("sid", out _)));
        Assert.True(rows.Single(row => row.GetProperty("role").GetString() == "z-role")
            .GetProperty("owner").GetBoolean());
    }

    [Fact]
    public async Task Empty_result_succeeds_but_manual_session_requires_explicit_identity()
    {
        using var cli = new CliSandbox();

        var missing = await cli.RunAsync("whoami");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("Pass --sid <id> or set PARLEY_ID", missing.Stderr);

        var empty = await cli.RunAsync("whoami", "--sid", "unknown-sid");
        empty.ShouldSucceed();
        Assert.Empty(empty.Stdout);
        Assert.Contains("No active channel memberships", empty.Stderr);
    }

    [Fact]
    public async Task Claude_whoami_repairs_all_memberships_after_clear()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cli = new CliSandbox();
        const string oldSid = "whoami-old-sid";
        const string newSid = "whoami-new-sid";
        cli.ConfigureClaudeAgents(AgentsJson(oldSid, pid: 5151, startedAt: 123456));

        var channel = cli.StartInteractive("integrations", "claude", "--sid", oldSid);
        try
        {
            await InitializeAsync(channel.Process);
            (await cli.RunAsync("join", "whoami-clear-a", "--as", "lead",
                "--sid", oldSid, "--wake", "claude")).ShouldSucceed();
            (await cli.RunAsync("join", "whoami-clear-b", "--as", "reviewer",
                "--sid", oldSid, "--wake", "claude")).ShouldSucceed();

            cli.UpdateClaudeAgents(AgentsJson(newSid, pid: 5151, startedAt: 123456));
            var result = await cli.RunWithEnvironmentAsync(
                new Dictionary<string, string> { ["CLAUDE_CODE_SESSION_ID"] = newSid },
                "whoami", "--json");
            result.ShouldSucceed();
            Assert.Equal(2, result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains("whoami-clear-a", result.Stdout);
            Assert.Contains("whoami-clear-b", result.Stdout);

            var first = await cli.RunAsync("members", "list", "whoami-clear-a", "--json");
            var second = await cli.RunAsync("members", "list", "whoami-clear-b", "--json");
            Assert.Contains(newSid, first.Stdout);
            Assert.Contains(newSid, second.Stdout);
            Assert.DoesNotContain(oldSid, first.Stdout);
            Assert.DoesNotContain(oldSid, second.Stdout);
        }
        finally
        {
            channel.Process.StandardInput.Close();
            await channel.Completion;
        }
    }

    private static Task<CliResult> Join(
        CliSandbox cli, string channel, string role, string sid) =>
        cli.RunAsync("join", channel, "--as", role, "--sid", sid, "--wake", "never");

    private static async Task InitializeAsync(Process process)
    {
        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","method":"initialize","id":1,"params":{"protocolVersion":"2025-06-18"}}""");
        await process.StandardInput.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = await process.StandardOutput.ReadLineAsync(timeout.Token);
        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""");
        await process.StandardInput.FlushAsync();
    }

    private static string AgentsJson(string sid, int pid, long startedAt) =>
        $$"""[{"pid":{{pid}},"startedAt":{{startedAt}},"sessionId":"{{sid}}"}]""";
}
