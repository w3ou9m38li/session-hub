using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Discovery;
using ClaudeLauncher.Infrastructure.Launch;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// Launching is the only thing this tool does towards ChatGPT and Codex besides reading, so
/// every refusal path here is one that keeps a stray process from starting.
/// </summary>
public sealed class CodexLaunchTests
{
    [Fact]
    public void CodexPathsHangOffTheHome()
    {
        var paths = CodexPaths.FromHome(@"D:\somewhere\.codex\");

        Assert.Equal(@"D:\somewhere\.codex", paths.Home);
        Assert.Equal(@"D:\somewhere\.codex\state_5.sqlite", paths.StateDatabase);
        Assert.Equal(@"D:\somewhere\.codex\sessions", paths.SessionsRoot);
        Assert.Equal(@"D:\somewhere\.codex\archived_sessions", paths.ArchivedSessionsRoot);
    }

    [Fact]
    public void DefaultHomeHonoursTheEnvironmentVariable()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var configured = Path.Combine(Path.GetTempPath(), "configured-codex-home");
            Environment.SetEnvironmentVariable("CODEX_HOME", configured);
            Assert.Equal(Path.GetFullPath(configured), CodexPaths.DefaultHome());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\me\.codex\sessions\a.jsonl", @"C:\Users\me\.codex\sessions\a.jsonl")]
    [InlineData(@"\\?\UNC\server\share\x", @"\\server\share\x")]
    [InlineData(@"E:\课题组\论文", @"E:\课题组\论文")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtendedLengthPrefixesAreRemoved(string? input, string? expected)
    {
        Assert.Equal(expected, CodexMetadataReader.NormalizePath(input));
    }

    [Theory]
    [InlineData("01a0c9c7-e537-7a30-9e7b-b508ed70ed67", true)]
    [InlineData("01A0C9C7-E537-7A30-9E7B-B508ED70ED67", true)]
    [InlineData("not-a-thread", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("01a0c9c7-e537-7a30-9e7b-b508ed70ed67; rm -rf", false)]
    public void OnlyAUuidIsAcceptedAsAThreadId(string? id, bool valid)
    {
        Assert.Equal(valid, CodexSessionLauncher.IsValidThreadId(id));
    }

    [Fact]
    public void ResumeRefusesABadIdBeforeTouchingAnything()
    {
        var result = CodexSessionLauncher.Resume("../../evil", Path.GetTempPath(), executable: @"C:\nowhere\codex.exe");
        Assert.Equal(ResumeOutcome.InvalidSessionId, result.Outcome);
    }

    [Fact]
    public void ResumeRefusesAMissingWorkingDirectory()
    {
        var result = CodexSessionLauncher.Resume(
            "01a0c9c7-e537-7a30-9e7b-b508ed70ed67", Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()),
            executable: @"C:\nowhere\codex.exe");
        Assert.Equal(ResumeOutcome.WorkingDirectoryMissing, result.Outcome);
    }

    [Fact]
    public void ResumeRefusesWhenNoTrustedCliExists()
    {
        var result = CodexSessionLauncher.Resume(
            "01a0c9c7-e537-7a30-9e7b-b508ed70ed67", Path.GetTempPath(),
            executable: Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".exe"));
        Assert.Equal(ResumeOutcome.ExecutableNotFound, result.Outcome);
    }

    [Fact]
    public void ChatGptLaunchReportsAMissingPackageInsteadOfGuessing()
    {
        var result = ChatGptLauncher.Open(new ChatGptInstallation { Found = false, FailureReason = "none" });
        Assert.Equal(LaunchOutcome.PackageNotFound, result.Outcome);
    }

    [Fact]
    public void ChatGptLaunchRefusesAnUnverifiedIdentity()
    {
        var result = ChatGptLauncher.Open(new ChatGptInstallation { Found = true, Aumid = "Some.Other_abc!App" });
        Assert.Equal(LaunchOutcome.IdentityMismatch, result.Outcome);
    }

    [Fact]
    public void ChatGptAumidIsTheStoreIdentity()
    {
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0!App", WindowsAppDiscovery.ChatGptAumid);
    }
}
