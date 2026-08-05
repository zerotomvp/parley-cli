using System.Diagnostics;
using System.Text.Json;
using ParleyCli.Serialization;
using Serilog;

namespace ParleyCli.Integrations;

public sealed class ClaudeAgentDiscovery
{
    public async Task<IReadOnlyList<ClaudeAgentInfo>?> ListAsync(CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "claude",
                ArgumentList = { "agents", "--json" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var json = await stdout;
                var error = await stderr;
                if (process.ExitCode != 0)
                {
                    Log.Verbose("[trace] claude agents discovery unavailable; exitCode={ExitCode} stderrLength={StderrLength}",
                        process.ExitCode, error.Length);
                    return null;
                }
                return JsonSerializer.Deserialize(json, ParleyJsonContext.Default.ClaudeAgentInfoArray);
            }
            finally
            {
                if (!process.HasExited)
                    try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or JsonException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            Log.Verbose(ex, "[trace] claude agents discovery failed");
            return null;
        }
    }
}

public sealed record ClaudeAgentInfo(int Pid, long StartedAt, string SessionId);
