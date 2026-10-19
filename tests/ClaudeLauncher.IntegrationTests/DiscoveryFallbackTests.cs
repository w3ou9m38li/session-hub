using System.Diagnostics;
using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// App discovery falls back to PowerShell only when Windows' packaging API cannot answer — the
/// state in which Get-AppxPackage is known to hang. Its timeout has to hold, and a process that
/// writes much to its error stream must not stop for want of a reader.
/// </summary>
public sealed class DiscoveryFallbackTests
{
    private static ProcessStartInfo PowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(argument);
        return psi;
    }

    [Fact]
    public void AProcessThatDoesNotAnswerIsStoppedAtTheTimeout()
    {
        var clock = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() =>
            WindowsAppDiscovery.Output(PowerShell("Start-Sleep -Seconds 30; 'late'"), TimeSpan.FromSeconds(3)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"waited {clock.Elapsed}");
    }

    [Fact]
    public async Task MuchWrittenToTheErrorStreamDoesNotStopIt()
    {
        var run = Task.Run(() => WindowsAppDiscovery.Output(
            PowerShell("[Console]::Error.Write('x' * 1048576); 'done'"), TimeSpan.FromSeconds(30)));

        // Stopped on a full error pipe, it would never answer: waited for a bounded time only.
        var output = await run.WaitAsync(TimeSpan.FromSeconds(40));
        Assert.Equal("done", output.Trim());
    }
}
