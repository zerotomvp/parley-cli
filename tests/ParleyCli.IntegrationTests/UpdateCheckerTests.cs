using System.Net;
using System.Text;
using ParleyCli.Updates;

namespace ParleyCli.IntegrationTests;

public sealed class UpdateCheckerTests
{
    [Fact]
    public async Task New_release_is_notified_once_and_cached()
    {
        var root = Path.Combine(Path.GetTempPath(), $"parley-update-tests-{Guid.NewGuid():N}");
        try
        {
            var handler = new StubHandler("""
                {"tag_name":"v1.2.0","html_url":"https://github.com/zerotomvp/parley-cli/releases/tag/v1.2.0"}
                """);
            using var http = new HttpClient(handler);
            var cache = Path.Combine(root, "update-check.json");
            var checker = new UpdateChecker(
                http, cache, new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero),
                new UpdateChecker.SemanticVersion(1, 1, 2),
                () => "brew upgrade parley");

            var first = new StringWriter();
            await checker.RunAsync(first);
            Assert.Contains("Parley 1.1.2 → 1.2.0", first.ToString());
            Assert.Contains("Upgrade with: brew upgrade parley", first.ToString());

            var second = new StringWriter();
            await checker.RunAsync(second);
            Assert.Equal(string.Empty, second.ToString());
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/opt/homebrew/Cellar/parley/1.2.0/bin/parley", "brew upgrade parley")]
    [InlineData("/home/linuxbrew/.linuxbrew/Cellar/parley/1.2.0/bin/parley", "brew upgrade parley")]
    [InlineData("C:\\Users\\dev\\scoop\\apps\\parley\\current\\parley.exe", "scoop update parley")]
    [InlineData("C:\\Users\\dev\\scoop\\shims\\parley.exe", "scoop update parley")]
    public void Package_manager_paths_produce_upgrade_command(string executable, string expected)
    {
        Assert.Equal(expected, InstallationDetector.UpgradeCommand(
            executable, "/home/dev", _ => false));
    }

    [Fact]
    public void Dotnet_tool_paths_distinguish_global_custom_and_manual_installs()
    {
        static bool HasStore(string path) => path.Replace('\\', '/').EndsWith("/.store/parley-cli");
        var profile = Path.Combine(Path.GetTempPath(), "parley-profile");
        var globalExecutable = Path.Combine(profile, ".dotnet", "tools", "parley");
        var customDirectory = Path.Combine(Path.GetTempPath(), "parley-tools");
        var customExecutable = Path.Combine(customDirectory, "parley");

        Assert.Equal("dotnet tool update --global parley-cli",
            InstallationDetector.UpgradeCommand(
                globalExecutable, profile, HasStore));
        Assert.Equal($"dotnet tool update --tool-path \"{customDirectory}\" parley-cli",
            InstallationDetector.UpgradeCommand(
                customExecutable, profile, HasStore));
        Assert.Null(InstallationDetector.UpgradeCommand(
            Path.Combine(Path.GetTempPath(), "manual", "parley"), profile, _ => false));
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
