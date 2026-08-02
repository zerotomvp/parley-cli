using System.Text.Json;

namespace ParleyCli.IntegrationTests;

public sealed class ClaudeChannelTests
{
    [Fact]
    public async Task Live_channel_is_detected_per_send_and_receives_wake_notice()
    {
        using var cli = new CliSandbox();
        var channel = cli.StartInteractive("claude-channel", "--sid", "recipient-sid");
        try
        {
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

            (await cli.RunAsync("join", "claude-wake", "--as", "sender", "--sid", "sender-sid", "--wake", "never")).ShouldSucceed();
            var joined = await cli.RunAsync("join", "claude-wake", "--as", "recipient", "--sid", "recipient-sid", "--wake", "claude");
            joined.ShouldSucceed();
            Assert.True(joined.Stderr.Contains("automatic Claude Code wake-up is available"),
                $"join did not detect the channel. join stderr:\n{joined.Stderr}");

            var sent = await cli.RunAsync("send", "claude-wake", "--as", "sender", "--sid", "sender-sid",
                "--to", "recipient", "-m", "Please review this.");
            sent.ShouldSucceed();
            Assert.Contains("woke recipient through Claude Code channel", sent.Stderr);

            var notice = await ReadLineAsync(channel.Process.StandardOutput);
            using var notification = JsonDocument.Parse(notice);
            Assert.Equal("notifications/claude/channel",
                notification.RootElement.GetProperty("method").GetString());
            var content = notification.RootElement.GetProperty("params").GetProperty("content").GetString();
            Assert.Contains("[Parley] Message #1", content);
            Assert.Contains("parley recv claude-wake --as recipient --last-seen", content);
        }
        finally
        {
            channel.Process.StandardInput.Close();
            await channel.Completion;
        }
    }

    private static async Task<string> ReadLineAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await reader.ReadLineAsync(timeout.Token)
            ?? throw new EndOfStreamException("Claude channel closed before producing the expected MCP frame.");
    }
}
