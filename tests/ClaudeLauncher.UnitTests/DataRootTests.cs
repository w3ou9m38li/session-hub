using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Safety;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// Where this tool's data lives is resolved once at startup and everything else follows from
/// it, so a wrong answer here puts the catalog somewhere the user did not ask for — or
/// somewhere it must never write at all.
/// </summary>
public sealed class DataRootTests
{
    private const string Preferred = @"D:\ClaudeLauncherData";
    private const string Fallback = @"C:\Users\someone\AppData\Local\ClaudeSessionLauncher";

    private static string Resolve(string? configured, params string[] usable) =>
        LauncherPaths.Resolve(configured, Preferred, Fallback,
            candidate => usable.Contains(candidate, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void TheDataDriveFolderIsUsedWhenItCanBeWrittenTo()
    {
        Assert.Equal(Preferred, Resolve(configured: null, Preferred));
    }

    [Fact]
    public void AnExplicitOverrideWins()
    {
        const string configured = @"E:\Elsewhere\LauncherData";
        Assert.Equal(configured, Resolve(configured, configured, Preferred));
    }

    [Fact]
    public void AMissingDataDriveFallsBackToTheUserProfile()
    {
        // Another machine with no D: must still start, not fail to find a root.
        Assert.Equal(Fallback, Resolve(configured: null));
    }

    [Fact]
    public void AnOverrideThatCannotBeWrittenToIsSkipped()
    {
        Assert.Equal(Preferred, Resolve(@"X:\NotThere", Preferred));
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"D:\ClaudeData\.claude")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude\data")]
    [InlineData(@"D:\x\claude-code-sessions")]
    public void AnOverridePointingAtForbiddenGroundIsRefusedEvenThoughTheUserAskedForIt(string configured)
    {
        // The probe would happily succeed for these; the policy is what stops them.
        Assert.Equal(Preferred, Resolve(configured, configured, Preferred));
    }

    [Fact]
    public void ThePreferredRootIsItselfALegalPlaceToWrite()
    {
        Assert.True(PathPolicy.MayBeDataRoot(LauncherPaths.PreferredDataRoot, out var reason), reason);
    }

    [Fact]
    public void ADriveRootIsNeverADataRoot()
    {
        Assert.False(PathPolicy.MayBeDataRoot(@"D:\", out var reason));
        Assert.Contains("驱动器根目录", reason);
    }
}
