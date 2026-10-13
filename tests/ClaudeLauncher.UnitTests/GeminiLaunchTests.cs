using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Launch;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// Continuing a Gemini CLI session is the one thing this tool does towards Gemini besides
/// reading, so every refusal path here is one that keeps a stray process from starting.
/// </summary>
public sealed class GeminiLaunchTests
{
    [Fact]
    public void GeminiPathsHangOffTheHome()
    {
        var paths = GeminiPaths.FromHome(@"D:\somewhere\.gemini\");

        Assert.Equal(@"D:\somewhere\.gemini", paths.Home);
        Assert.Equal(@"D:\somewhere\.gemini\tmp", paths.ProjectsRoot);
    }

    [Theory]
    [InlineData("0a1b2c3d-0000-4000-8000-00000000000a", true)]
    [InlineData("0A1B2C3D-0000-4000-8000-00000000000A", true)]
    [InlineData("0a1b2c3d", false)]
    [InlineData("0a1b2c3d-0000-4000-8000-00000000000a --yolo", false)]
    [InlineData("0a1b2c3d-0000-4000-8000-00000000000a\" & calc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyASessionIdIsPassedOn(string? id, bool valid) =>
        Assert.Equal(valid, GeminiSessionLauncher.IsValidSessionId(id));

    [Fact]
    public void AnInvalidIdStartsNothing()
    {
        var result = GeminiSessionLauncher.Resume("--yolo", Path.GetTempPath(), executable: @"C:\nowhere\gemini.cmd");
        Assert.Equal(ResumeOutcome.InvalidSessionId, result.Outcome);
    }

    [Fact]
    public void AMissingFolderStartsNothing()
    {
        var result = GeminiSessionLauncher.Resume("0a1b2c3d-0000-4000-8000-00000000000a",
            Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("n")),
            executable: @"C:\nowhere\gemini.cmd");
        Assert.Equal(ResumeOutcome.WorkingDirectoryMissing, result.Outcome);
        Assert.Contains("工作目录不存在", result.Message);
    }

    [Fact]
    public void NoGeminiCliStartsNothing()
    {
        var result = GeminiSessionLauncher.Resume("0a1b2c3d-0000-4000-8000-00000000000a", Path.GetTempPath(),
            executable: Path.Combine(Path.GetTempPath(), "no-such-gemini-" + Guid.NewGuid().ToString("n") + ".cmd"));
        Assert.Equal(ResumeOutcome.ExecutableNotFound, result.Outcome);
        Assert.Contains("npm install -g @google/gemini-cli", result.Message);
    }
}
