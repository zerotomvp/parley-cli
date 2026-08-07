using System.Text.Json;

namespace ParleyCli.IntegrationTests;

public sealed class PiChannelTests
{
    [Fact]
    public async Task Live_extension_bridge_is_detected_and_acknowledges_wake_submission()
    {
        using var cli = new CliSandbox();
        var bridge = cli.StartInteractive("pi-channel", "--sid", "pi-recipient-sid");
        try
        {
            using (var ready = JsonDocument.Parse(await ReadLineAsync(bridge.Process.StandardOutput)))
            {
                Assert.Equal("ready", ready.RootElement.GetProperty("type").GetString());
                Assert.Equal("pi-recipient-sid", ready.RootElement.GetProperty("sid").GetString());
            }

            (await cli.RunAsync("join", "pi-wake", "--as", "sender", "--sid", "sender-sid",
                "--wake", "never")).ShouldSucceed();
            var joined = await cli.RunAsync("join", "pi-wake", "--as", "recipient",
                "--sid", "pi-recipient-sid", "--wake", "pi");
            joined.ShouldSucceed();
            Assert.Contains("live Pi extension endpoint is available", joined.Stderr);

            var sending = cli.RunAsync("send", "pi-wake", "--as", "sender", "--sid", "sender-sid",
                "--to", "recipient", "-m", "Please inspect this.");
            using var wake = JsonDocument.Parse(await ReadLineAsync(bridge.Process.StandardOutput));
            Assert.Equal("wake", wake.RootElement.GetProperty("type").GetString());
            var id = wake.RootElement.GetProperty("id").GetString();
            var notification = wake.RootElement.GetProperty("notification").GetString();
            Assert.Contains("[Parley #1 pending · pi-wake · recipient]", notification);
            Assert.Contains("parley recv pi-wake --as recipient --last-seen", notification);

            await bridge.Process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(new { id, success = true }));
            await bridge.Process.StandardInput.FlushAsync();

            var sent = await sending;
            sent.ShouldSucceed();
            Assert.Contains("woke recipient through Pi extension", sent.Stderr);
        }
        finally
        {
            bridge.Process.StandardInput.Close();
            await bridge.Completion;
        }
    }

    [Fact]
    public async Task Extension_rejection_is_reported_without_losing_the_message()
    {
        using var cli = new CliSandbox();
        var bridge = cli.StartInteractive("pi-channel", "--sid", "pi-reject-sid");
        try
        {
            _ = await ReadLineAsync(bridge.Process.StandardOutput);
            (await cli.RunAsync("join", "pi-reject", "--as", "sender", "--sid", "sender-sid",
                "--wake", "never")).ShouldSucceed();
            (await cli.RunAsync("join", "pi-reject", "--as", "recipient", "--sid", "pi-reject-sid",
                "--wake", "pi")).ShouldSucceed();

            var sending = cli.RunAsync("send", "pi-reject", "--as", "sender", "--sid", "sender-sid",
                "--to", "recipient", "-m", "This remains durable.");
            using var wake = JsonDocument.Parse(await ReadLineAsync(bridge.Process.StandardOutput));
            var id = wake.RootElement.GetProperty("id").GetString();
            await bridge.Process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(new { id, success = false, error = "session is shutting down" }));
            await bridge.Process.StandardInput.FlushAsync();

            var sent = await sending;
            sent.ShouldSucceed();
            Assert.Contains("message remains delivered", sent.Stderr);
            Assert.Contains("waking recipient failed", sent.Stderr);

            var received = await cli.RunAsync("recv", "pi-reject", "--as", "recipient",
                "--sid", "pi-reject-sid", "--last-seen", "0");
            received.ShouldSucceed();
            Assert.Contains("This remains durable.", received.Stdout);
        }
        finally
        {
            bridge.Process.StandardInput.Close();
            await bridge.Completion;
        }
    }

    private static async Task<string> ReadLineAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await reader.ReadLineAsync(timeout.Token)
            ?? throw new EndOfStreamException("Pi channel bridge closed before emitting a frame.");
    }
}
