using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Launch;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// The claude:// links the official app registers. Ids are validated before they reach a URL
/// so nothing user-controlled can be smuggled into one.
/// </summary>
public sealed class DeepLinkTests
{
    private const string Uuid = "57f8816e-8b84-4dde-9fe0-abda4a690764";
    private const string DesktopId = "local_d4b04d95-d195-4869-8acf-4b693e28a0ff";

    [Fact]
    public void CliResumeLinkIsTheImportRoutesOwnHost()
    {
        // Claude routes by host: under claude.ai this would be a web page and import nothing.
        Assert.Equal($"claude://resume?session={Uuid}", ClaudeDeepLink.BuildResumeCli(Uuid));
    }

    [Fact]
    public void DesktopOpenLinkMatchesTheOfficialShape()
    {
        Assert.Equal($"claude://claude.ai/epitaxy/{DesktopId}", ClaudeDeepLink.BuildOpenDesktop(DesktopId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("57f8816e-8b84-4dde-9fe0-abda4a69076")]     // one char short
    [InlineData("57f8816e-8b84-4dde-9fe0-abda4a690764x")]
    public void MalformedCliIdIsRefused(string id)
    {
        Assert.Null(ClaudeDeepLink.BuildResumeCli(id));
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("57f8816e-8b84-4dde-9fe0-abda4a690764&other=1")]
    [InlineData("57f8816e-8b84-4dde-9fe0-abda4a690764 extra")]
    public void InjectionAttemptsNeverReachTheUrl(string id)
    {
        Assert.Null(ClaudeDeepLink.BuildResumeCli(id));
        Assert.Null(ClaudeDeepLink.BuildOpenDesktop(id));
    }

    [Fact]
    public void DesktopIdMustCarryItsLocalPrefix()
    {
        // A bare UUID is a CLI id, not a desktop session id; the two must not be interchangeable.
        Assert.Null(ClaudeDeepLink.BuildOpenDesktop(Uuid));
    }

    [Fact]
    public void CliIdMustNotCarryTheDesktopPrefix()
    {
        Assert.Null(ClaudeDeepLink.BuildResumeCli(DesktopId));
    }

    [Fact]
    public void BackendSelectsTheCorrectRoute()
    {
        var (desktop, _) = ClaudeDeepLink.BuildFor(ConversationKey.DesktopCode, DesktopId, Uuid);
        var (cli, _) = ClaudeDeepLink.BuildFor(ConversationKey.CliCode, null, Uuid);

        Assert.Contains("/epitaxy/", desktop!, StringComparison.Ordinal);
        Assert.StartsWith("claude://resume?session=", cli!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConversationKey.Chat)]
    [InlineData(ConversationKey.Cowork)]
    public void UnverifiedBackendsHaveNoLink(string backend)
    {
        var (uri, outcome) = ClaudeDeepLink.BuildFor(backend, DesktopId, Uuid);

        Assert.Null(uri);
        Assert.Equal(DeepLinkOutcome.UnsupportedBackend, outcome);
    }

    [Fact]
    public void ACodexTaskOpensThroughTheAppsOwnThreadLink()
    {
        // The link ChatGPT itself builds for a task, lower-cased as its ids are written.
        Assert.Equal("codex://threads/01a055b1-54b9-7692-bdca-7f3bd37baf40",
            ChatGptDeepLink.BuildThread("01A055B1-54B9-7692-BDCA-7F3BD37BAF40"));
        Assert.True(ChatGptDeepLink.CanOpen("01a055b1-54b9-7692-bdca-7f3bd37baf40"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("new")]                                              // the app's own "new task" route
    [InlineData("01a055b1-54b9-7692-bdca-7f3bd37baf40?hostId=x")]
    [InlineData("01a055b1-54b9-7692-bdca-7f3bd37baf40/../settings")]
    [InlineData("local_01a055b1-54b9-7692-bdca-7f3bd37baf40")]
    public void NothingButATaskIdReachesTheCodexLink(string? id)
    {
        Assert.Null(ChatGptDeepLink.BuildThread(id));
        Assert.False(ChatGptDeepLink.CanOpen(id));
    }

    [Fact]
    public void LinkCarriesNoVersionOrFilePath()
    {
        // The whole point: nothing here changes when Claude updates itself.
        var link = ClaudeDeepLink.BuildResumeCli(Uuid)!;

        Assert.DoesNotContain("WindowsApps", link, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".exe", link, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", link, StringComparison.Ordinal);
    }
}
