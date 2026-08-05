using System.Diagnostics;

namespace ParleyCli.IntegrationTests;

internal sealed class CliSandbox : IDisposable
{
    private readonly string _root;
    private readonly string _store;
    private readonly string _fakeBin;
    private readonly string _fileName;
    private readonly string[] _prefixArguments;

    public CliSandbox()
    {
        _root = FindRepositoryRoot();
        _store = Path.Combine(Path.GetTempPath(), $"parley-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_store);
        _fakeBin = Path.Combine(_store, "bin");
        Directory.CreateDirectory(_fakeBin);

        var supplied = Environment.GetEnvironmentVariable("PARLEY_TEST_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            var executable = Path.GetFullPath(supplied);
            if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                _fileName = DotnetHost();
                _prefixArguments = [executable];
            }
            else
            {
                _fileName = executable;
                _prefixArguments = [];
            }
        }
        else
        {
            var configuration = Environment.GetEnvironmentVariable("CONFIGURATION")
                ?? new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? "Debug";
            var dll = Path.Combine(_root, "bin", configuration, "net10.0", "parley-cli.dll");
            _fileName = DotnetHost();
            _prefixArguments = [dll];
        }
    }

    public string Store => _store;

    public async Task<CliResult> RunAsync(params string[] arguments) =>
        await Start(arguments).Completion;

    public async Task<CliResult> RunWithEnvironmentAsync(
        IReadOnlyDictionary<string, string> environment, params string[] arguments) =>
        await Start(arguments, closeInput: true, environment: environment).Completion;

    public RunningCli Start(params string[] arguments) => Start(arguments, closeInput: true);

    public RunningCli StartInteractive(params string[] arguments) => Start(arguments, closeInput: false);

    private RunningCli Start(string[] arguments, bool closeInput,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = _fileName,
            WorkingDirectory = _root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in _prefixArguments.Concat(arguments))
            start.ArgumentList.Add(argument);

        start.Environment["PARLEY_HOME"] = _store;
        start.Environment.Remove("PARLEY_ID");
        start.Environment.Remove("CODEX_THREAD_ID");
        start.Environment.Remove("CLAUDE_CODE_SESSION_ID");
        if (environment is not null)
            foreach (var (name, value) in environment)
                start.Environment[name] = value;
        start.Environment["NO_COLOR"] = "1";
        start.Environment["PATH"] = _fakeBin;

        var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start Parley.");
        if (closeInput) process.StandardInput.Close();
        return new RunningCli(process, captureOutput: closeInput);
    }

    public string Transcript(string channel) =>
        Path.Combine(_store, "channels", $"{channel}.jsonl");

    public string Cursor(string channel, string sid) =>
        Path.Combine(_store, "channels", $"{channel}.{sid}.cursor");

    public void Dispose()
    {
        if (Directory.Exists(_store)) Directory.Delete(_store, recursive: true);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "parley-cli.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate parley-cli.csproj from the test output directory.");
    }

    public void ConfigureRunningCodex(string socketPath)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The fake Codex Unix-socket fixture is Unix-only.");

        var path = Path.Combine(_fakeBin, "codex");
        var json = System.Text.Json.JsonSerializer.Serialize(new { status = "running", socketPath });
        File.WriteAllText(path, $"#!/bin/sh\nprintf '%s\\n' '{json}'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void ConfigureClaudeAgents(string json)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The fake Claude shell fixture is Unix-only.");

        UpdateClaudeAgents(json);
        var state = Path.Combine(_store, "claude-agents.json");
        var path = Path.Combine(_fakeBin, "claude");
        File.WriteAllText(path, $"#!/bin/sh\nexec /bin/cat '{state}'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void UpdateClaudeAgents(string json) =>
        File.WriteAllText(Path.Combine(_store, "claude-agents.json"), json);

    private static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? Environment.ProcessPath
        ?? "dotnet";
}

internal sealed class RunningCli
{
    public RunningCli(Process process, bool captureOutput = true)
    {
        Process = process;
        Completion = captureOutput ? CompleteAsync(process) : WaitOnlyAsync(process);
    }

    public Process Process { get; }
    public Task<CliResult> Completion { get; }

    private static async Task<CliResult> WaitOnlyAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new CliResult(process.ExitCode, "", "");
    }

    private static async Task<CliResult> CompleteAsync(Process process)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new CliResult(process.ExitCode, await stdout, await stderr);
    }
}

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr)
{
    public void ShouldSucceed() => Assert.True(ExitCode == 0,
        $"Expected exit code 0, got {ExitCode}.\nstdout:\n{Stdout}\nstderr:\n{Stderr}");
}
