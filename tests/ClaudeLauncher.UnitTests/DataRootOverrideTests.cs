using ClaudeLauncher.Infrastructure;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// A copy started with CLAUDE_LAUNCHER_DATA_DIR says so in its title — but only when that
/// directory is really the one in use. A refused or unwritable one falls back to the everyday
/// catalog, and the title must not claim isolation it does not have.
/// </summary>
public sealed class DataRootOverrideTests
{
    [Theory]
    [InlineData(@"D:\Temp\testdata", @"D:\Temp\testdata", true)]
    [InlineData(@"D:\Temp\testdata\", @"D:\Temp\testdata", true)]         // trailing separator
    [InlineData(@"d:\temp\TESTDATA", @"D:\Temp\testdata", true)]          // case
    [InlineData(@"D:\Temp\testdata", @"D:\ClaudeLauncherData", false)]    // refused: fell back
    [InlineData(null, @"D:\ClaudeLauncherData", false)]
    [InlineData("   ", @"D:\ClaudeLauncherData", false)]
    [InlineData("D:\\bad\0path", @"D:\ClaudeLauncherData", false)]        // not a path at all
    public void OnlyAnOverrideInUseCounts(string? configured, string dataRoot, bool expected)
    {
        Assert.Equal(expected, LauncherPaths.ConfiguredRootInUse(configured, dataRoot));
    }
}
