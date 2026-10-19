using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// Claude and ChatGPT installed some other way than the store packages this launcher was first
/// verified against — found, and still held to who made them.
/// </summary>
public sealed class InstallVariantsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-variants", Guid.NewGuid().ToString("n"));

    public InstallVariantsTests() => Directory.CreateDirectory(_dir);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public void TheApplicationIdIsReadFromTheManifest()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="ChatGPT" Executable="app\ChatGPT.exe" EntryPoint="Windows.FullTrustApplication"/>
                <Application Id="Helper" Executable="app\helper.exe"/>
              </Applications>
            </Package>
            """;
        Assert.Equal("ChatGPT", InstallVariants.FirstApplicationId(manifest));
        Assert.Null(InstallVariants.FirstApplicationId("<not xml"));
        Assert.Null(InstallVariants.FirstApplicationId("<Package/>"));

        // Read from the package without standing in the way of an update that replaces it.
        var package = Path.Combine(_dir, "package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "AppxManifest.xml"), manifest);
        using (new FileStream(Path.Combine(package, "AppxManifest.xml"), FileMode.Open, FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
            Assert.Equal("ChatGPT", InstallVariants.ApplicationIdOf(package));
    }

    [Fact]
    public void OnlyOpenAisPackagesAreLaunchedAsChatGpt()
    {
        Assert.True(InstallVariants.IsChatGptAumid("OpenAI.Codex_2p2nqsd0c76g0!App"));
        Assert.True(InstallVariants.IsChatGptAumid("OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT"));
        Assert.False(InstallVariants.IsChatGptAumid("OpenAI.Codex_someoneelse!App"));
        Assert.False(InstallVariants.IsChatGptAumid("OpenAI.Codex_2p2nqsd0c76g0!"));
        Assert.False(InstallVariants.IsChatGptAumid(null));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\WindowsApps\\Claude_2\\app\\Claude.exe\" \"%1\"", "C:\\Program Files\\WindowsApps\\Claude_2\\app\\Claude.exe")]
    [InlineData("C:\\Users\\a\\AppData\\Local\\AnthropicClaude\\claude.exe --url %1", "C:\\Users\\a\\AppData\\Local\\AnthropicClaude\\claude.exe")]
    [InlineData("  ", null)]
    [InlineData("\"", null)]
    public void AProtocolCommandNamesItsProgram(string command, string? expected) =>
        Assert.Equal(expected, InstallVariants.CommandExecutable(command));

    [Fact]
    public void TheStandaloneInstallIsFoundWithItsRealProfile()
    {
        var launcher = Touch("local", "AnthropicClaude", "claude.exe");
        Directory.CreateDirectory(Path.Combine(_dir, "roaming", "Claude"));

        var found = InstallVariants.FindStandaloneClaude(
            Path.Combine(_dir, "local"), Path.Combine(_dir, "roaming"), protocolCommand: null, isAnthropics: _ => true);

        Assert.NotNull(found);
        Assert.Equal(ClaudeFlavor.Standalone, found.Flavor);
        Assert.Equal(launcher, found.Executable);
        Assert.Equal(Path.Combine(_dir, "roaming", "Claude"), found.ProfileRoot);
    }

    [Fact]
    public void WithoutItsLauncherTheNewestAppCopyIsUsed()
    {
        Touch("local", "AnthropicClaude", "app-0.9.0", "claude.exe");
        var newest = Touch("local", "AnthropicClaude", "app-0.10.2", "claude.exe");

        var found = InstallVariants.FindStandaloneClaude(
            Path.Combine(_dir, "local"), Path.Combine(_dir, "roaming"), null,
            isAnthropics: exe => !exe.EndsWith(Path.Combine("AnthropicClaude", "claude.exe"), StringComparison.OrdinalIgnoreCase));

        Assert.Equal(newest, found?.Executable);
        Assert.Null(found?.ProfileRoot);                 // no profile yet: none is made up
    }

    [Fact]
    public void AProgramThatDoesNotSayItIsAnthropicsIsNotClaude()
    {
        Touch("local", "AnthropicClaude", "claude.exe");
        var other = Touch("elsewhere", "claude.exe");

        Assert.Null(InstallVariants.FindStandaloneClaude(
            Path.Combine(_dir, "local"), Path.Combine(_dir, "roaming"), $"\"{other}\" \"%1\"", isAnthropics: _ => false));
    }

    [Fact]
    public void TheStoreCopysProtocolHandlerIsNotAStandaloneInstall()
    {
        var store = Touch("Program Files", "WindowsApps", "Claude_2_x64__pzs8sxrjxfjjc", "app", "Claude.exe");

        Assert.Null(InstallVariants.FindStandaloneClaude(
            Path.Combine(_dir, "local"), Path.Combine(_dir, "roaming"), $"\"{store}\" \"%1\"", isAnthropics: _ => true));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
