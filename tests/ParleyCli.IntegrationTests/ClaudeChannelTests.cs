using System.Text.Json;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

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
            // A join probe only establishes that the endpoint exists. It must not race the MCP
            // initialization handshake, which Claude can complete after the CLI has already joined.
            (await cli.RunAsync("join", "claude-wake", "--as", "sender", "--sid", "sender-sid", "--wake", "never")).ShouldSucceed();
            var joined = await cli.RunAsync("join", "claude-wake", "--as", "recipient", "--sid", "recipient-sid", "--wake", "claude");
            joined.ShouldSucceed();
            Assert.True(joined.Stderr.Contains("automatic Claude Code wake-up is available"),
                $"join did not detect the uninitialized channel. join stderr:\n{joined.Stderr}");

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
            Assert.Contains("[Parley] Message #1", content);
            Assert.Contains("parley recv claude-wake --as recipient --last-seen", content);
            Assert.DoesNotContain("--wait", content);

            var sentAgain = await cli.RunAsync("send", "claude-wake", "--as", "sender", "--sid", "sender-sid",
                "--to", "recipient", "-m", "One more request.");
            sentAgain.ShouldSucceed();
            Assert.Contains("woke recipient through Claude Code channel", sentAgain.Stderr);
            var secondNotice = await ReadLineAsync(channel.Process.StandardOutput);
            using var secondNotification = JsonDocument.Parse(secondNotice);
            Assert.Contains("Message #2", secondNotification.RootElement
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
        var channel = cli.StartInteractive("claude-channel", "--sid", "resilient-sid");
        try
        {
            await using (var abandoned = new NamedPipeClientStream(
                ".", PipeName(cli.Store, "resilient-sid"), PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await abandoned.ConnectAsync(timeout.Token);
                await using var writer = new StreamWriter(abandoned, new UTF8Encoding(false), leaveOpen: true)
                    { AutoFlush = true };
                await writer.WriteLineAsync("abandoned wake");
            }

            await channel.Process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"initialize","id":1,"params":{"protocolVersion":"2025-06-18"}}""");
            await channel.Process.StandardInput.FlushAsync();
            _ = await ReadLineAsync(channel.Process.StandardOutput);
            await channel.Process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""");
            await channel.Process.StandardInput.FlushAsync();

            // The abandoned notification can reach MCP stdout, but its broken acknowledgement
            // connection must be isolated so that a later probe gets a fresh pipe instance.
            _ = await ReadLineAsync(channel.Process.StandardOutput);
            var joined = await cli.RunAsync("join", "resilient", "--as", "recipient",
                "--sid", "resilient-sid", "--wake", "claude");
            joined.ShouldSucceed();
            Assert.Contains("automatic Claude Code wake-up is available", joined.Stderr);
        }
        finally
        {
            channel.Process.StandardInput.Close();
            await channel.Completion;
        }
    }

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
