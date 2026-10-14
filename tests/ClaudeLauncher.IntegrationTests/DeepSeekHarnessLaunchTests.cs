using ClaudeLauncher.Infrastructure.Launch;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// DeepSeek Harness is started the way the user starts it: their shortcut, or the start script
/// beside a home it was deployed with — never the stop script beside it, and never by this tool
/// starting the server itself. Nothing here launches anything.
/// </summary>
public sealed class DeepSeekHarnessLaunchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-dsh-launch", Guid.NewGuid().ToString("n"));

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void TheUsersShortcutComesFirstThenTheStartScriptBesideAHome()
    {
        var desktop = Path.Combine(_dir, "desktop");
        var programs = Path.Combine(_dir, "programs");
        Directory.CreateDirectory(desktop);
        var stop = Touch("deploy", "停止DeepSeekHarness.cmd");
        var start = Touch("deploy", "启动DeepSeekHarness.cmd");
        Touch("deploy", "data", "sessions", "keep");
        var home = Path.Combine(_dir, "deploy", "data");
        (string, bool)[] folders = [(desktop, false), (programs, true)];

        Assert.Equal(start, DeepSeekHarnessLauncher.Find([home], folders));     // no shortcut: the start script, not the stop one
        Assert.NotEqual(stop, DeepSeekHarnessLauncher.Find([home], folders));
        Assert.Null(DeepSeekHarnessLauncher.Find([Path.Combine(_dir, "deploy")], folders));   // only beside a home named data

        var nested = Touch("programs", "Tools", "DeepSeek Harness.lnk");
        Assert.Equal(nested, DeepSeekHarnessLauncher.Find([home], folders));    // the Start menu, a folder down
        Touch("desktop", "Stop DeepSeek Harness.lnk");
        Touch("desktop", "DeepSeek Harness 停止.lnk");
        Assert.Equal(nested, DeepSeekHarnessLauncher.Find([home], folders));    // a shortcut that stops it is never taken
        var shortcut = Touch("desktop", "DeepSeek Harness.lnk");
        Assert.Equal(shortcut, DeepSeekHarnessLauncher.Find([home], folders));  // the desktop first
        Assert.Equal("DeepSeek Harness", DeepSeekHarnessLauncher.NameOf(shortcut));
    }

    [Fact]
    public void NothingToStartItBySaysSoAndStartsNothing()
    {
        Assert.Null(DeepSeekHarnessLauncher.Find([], [(Path.Combine(_dir, "nowhere"), false)]));
        var result = DeepSeekHarnessLauncher.Open(null);
        Assert.Equal(LaunchOutcome.PackageNotFound, result.Outcome);
        Assert.Contains("DeepSeek Harness", result.Message);
        Assert.Equal(LaunchOutcome.PackageNotFound, DeepSeekHarnessLauncher.Open(Path.Combine(_dir, "gone.lnk")).Outcome);
    }

    [Theory]
    [InlineData("启动DeepSeekHarness.cmd", true)]
    [InlineData("打开 DeepSeek Harness.bat", true)]
    [InlineData("start-deepseek.cmd", true)]
    [InlineData("停止DeepSeekHarness.cmd", false)]
    [InlineData("stop-deepseek.cmd", false)]
    [InlineData("start-other-tool.cmd", false)]
    [InlineData("DeepSeek 卸载 uninstall.cmd", false)]
    public void AStartScriptNamesDeepSeekAndSaysStart(string name, bool start) =>
        Assert.Equal(start, DeepSeekHarnessLauncher.IsStartScript(name));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
