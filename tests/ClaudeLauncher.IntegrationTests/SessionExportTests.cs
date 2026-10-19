using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The export exists so the user can see which folders are blocking an import. Its failure
/// modes mislead rather than crash — counting a conversation twice, comparing trust with the
/// wrong separator, calling a slugged folder name a missing directory — so each is pinned here.
/// </summary>
public sealed class SessionExportTests : IDisposable
{
    private readonly string _dir;

    public SessionExportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-export", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private string WriteConfig(string json)
    {
        var root = Path.Combine(_dir, "config", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".claude.json"), json, new UTF8Encoding(false));
        return root;
    }

    private static SessionMetadata Cli(string id, string? project) =>
        new()
        {
            Key = new ConversationKey(ConversationKey.CliCode, "acct", "org", id),
            Title = "会话 " + id,
            ProjectRef = project,
            SearchKey = id,
            SourceRevision = "r1",
        };

    /// <summary>A Desktop record, optionally linked to the CLI transcript it was imported from.</summary>
    private static SessionMetadata Desktop(string id, string? project, string? cliId = null) =>
        new()
        {
            Key = new ConversationKey(ConversationKey.DesktopCode, "acct", "org", "local_" + id),
            CliSessionId = cliId,
            Title = "会话 " + id,
            ProjectRef = project,
            SearchKey = id,
            SourceRevision = "r1",
        };

    private string OutDir() => Path.Combine(_dir, "out", Guid.NewGuid().ToString("n"));

    private string Untrusted(IReadOnlyList<SessionMetadata> rows, string configRoot)
    {
        var dir = OutDir();
        SessionExporter.Write(rows, configRoot, dir);
        return File.ReadAllText(Path.Combine(dir, "未信任目录.md"));
    }

    [Fact]
    public void LinkedDesktopAndCliRowsAreOneConversation()
    {
        // The Desktop record and the transcript it was imported from are the same conversation.
        // Summing them would report twice the history the user has, and would list a session
        // that is already in the sidebar as something still to recover.
        var result = SessionExporter.Write(
            [Cli("abc", @"G:\x"), Desktop("d1", @"G:\x", cliId: "abc")],
            WriteConfig(@"{""projects"":{}}"), OutDir());

        Assert.Equal(1, result.Conversations);
        Assert.Equal(1, result.AlreadyInSidebar);
        Assert.Equal(0, result.RecoverableSessions);
    }

    [Fact]
    public void AnUnlinkedCliRowStaysItsOwnConversation()
    {
        var live = Path.Combine(_dir, "live");
        Directory.CreateDirectory(live);

        var result = SessionExporter.Write(
            [Cli("abc", live), Desktop("d1", live, cliId: "other")],
            WriteConfig(@"{""projects"":{}}"), OutDir());

        // Dedupe keys on the link, not on the folder: two unrelated conversations stay two.
        Assert.Equal(2, result.Conversations);
        Assert.Equal(1, result.AlreadyInSidebar);
        Assert.Equal(1, result.RecoverableSessions);
    }

    [Fact]
    public void TrustMatchesAcrossSeparatorSpellings()
    {
        // Claude's own config mixes G:/ABC and G:\ABC for the same folder. Comparing raw
        // strings would report a trusted folder as untrusted and send the user to re-trust it.
        var config = WriteConfig(@"{""projects"":{""G:/ABC中期"":{""hasTrustDialogAccepted"":true}}}");

        var report = Untrusted([Cli("a", @"G:\ABC中期")], config);

        Assert.DoesNotContain("ABC中期", report);
        Assert.Contains("**0** 个目录", report);
    }

    [Fact]
    public void UntrustedFolderIsListedWithItsSessionCount()
    {
        var config = WriteConfig(@"{""projects"":{""G:\\trusted"":{""hasTrustDialogAccepted"":true}}}");

        var report = Untrusted(
            [Cli("a", @"G:\WCX"), Cli("b", @"G:\WCX"), Cli("c", @"G:\trusted")], config);

        Assert.Contains(@"G:\WCX", report);
        Assert.DoesNotContain(@"| `G:\trusted` |", report);
        Assert.Contains("| 2 | 2 |", report);
    }

    [Fact]
    public void TrustRecordedAsFalseIsNotTreatedAsTrusted()
    {
        var config = WriteConfig(@"{""projects"":{""G:\\WCX"":{""hasTrustDialogAccepted"":false}}}");

        // Being present in the config is not the same as being trusted; only the flag decides.
        Assert.Contains(@"G:\WCX", Untrusted([Cli("a", @"G:\WCX")], config));
    }

    [Fact]
    public void SluggedFolderNameIsNotReportedAsAMissingDirectory()
    {
        // A transcript with no cwd falls back to the slug "G--zxl-WLS". Directory.Exists says
        // no — but the folder is not gone, we just never recorded its path. Printing "否" here
        // would tell the user to give up on a session that is fine.
        var report = Untrusted([Cli("a", "G--zxl-WLS")], WriteConfig(@"{""projects"":{}}"));

        Assert.Contains("路径未知（只剩目录名）", report);
        Assert.DoesNotContain("| **否** |", report);
    }

    [Fact]
    public void RecoverableCountExcludesFoldersThatNoLongerExist()
    {
        var live = Path.Combine(_dir, "live");
        Directory.CreateDirectory(live);
        var gone = Path.Combine(_dir, "gone");   // deliberately never created

        var result = SessionExporter.Write(
            [Cli("a", live), Cli("b", gone)], WriteConfig(@"{""projects"":{}}"), OutDir());

        // Two untrusted folders, but trusting the deleted one would accomplish nothing.
        Assert.Equal(2, result.WorkingDirectories);
        Assert.Equal(0, result.TrustedDirectories);
        Assert.Equal(1, result.RecoverableSessions);
    }

    [Fact]
    public void UnreadableConfigReportsNothingAsTrusted()
    {
        // Guessing "trusted" from a broken config would hide folders the user must act on.
        Assert.Contains(@"G:\WCX", Untrusted([Cli("a", @"G:\WCX")], WriteConfig("{ this is not json")));
    }

    private const string TrustsDemo = @"{""projects"":{""G:/demo"":{""hasTrustDialogAccepted"":true}}}";

    [Fact]
    public void AConfigClaudeCodeIsWritingIsReadWithoutStandingInItsWay()
    {
        var root = WriteConfig(TrustsDemo);

        // Held the way Claude Code holds it while it writes it in place or renames a new copy
        // over it: reading must not fail, nor make Claude Code's save fail.
        using (new FileStream(Path.Combine(root, ".claude.json"), FileMode.Open, FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
            Assert.Contains(@"G:\demo", SessionExporter.ReadTrustedDirectories(root));
    }

    [Fact]
    public void AConfigHeldForAMomentIsReadOnceItIsFree()
    {
        var root = WriteConfig(TrustsDemo);
        using var held = new FileStream(Path.Combine(root, ".claude.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var failed = new List<int>();

        // Claude Code lets go between two tries.
        var trusted = SessionExporter.ReadTrustedDirectories(root, attempt =>
        {
            failed.Add(attempt);
            held.Dispose();
        });

        Assert.Contains(@"G:\demo", trusted);
        Assert.Equal([1], failed);
    }

    [Fact]
    public void AConfigThatStaysHeldFailsTheExportRatherThanTrustNothing()
    {
        // "Nothing trusted" would send the user to trust every folder again.
        var root = WriteConfig(TrustsDemo);
        using (new FileStream(Path.Combine(root, ".claude.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => SessionExporter.ReadTrustedDirectories(root));
    }

    [Fact]
    public void EveryConversationReachesTheCsv()
    {
        var dir = OutDir();
        var rows = Enumerable.Range(0, 25).Select(i => Cli("s" + i, @"G:\x")).ToList();

        var result = SessionExporter.Write(rows, WriteConfig(@"{""projects"":{}}"), dir);

        var lines = File.ReadAllLines(Path.Combine(dir, "全部对话.csv"));
        Assert.Equal(25, result.Conversations);
        Assert.Equal(26, lines.Length);        // header + one line per conversation
    }

    [Fact]
    public void TitlesContainingCommasStayInOneCsvColumn()
    {
        var dir = OutDir();
        var row = Cli("a", @"G:\x") with { Title = "a,b \"quoted\" c" };

        SessionExporter.Write([row], WriteConfig(@"{""projects"":{}}"), dir);

        var lines = File.ReadAllLines(Path.Combine(dir, "全部对话.csv"));
        Assert.Contains("\"a,b \"\"quoted\"\" c\"", lines[1]);
    }

    [Fact]
    public void IndexSeparatesSidebarConversationsFromTranscriptOnlyOnes()
    {
        var dir = OutDir();
        SessionExporter.Write(
            [Cli("a", @"G:\x"), Desktop("d1", @"G:\x", cliId: "b"), Cli("b", @"G:\x")],
            WriteConfig(@"{""projects"":{}}"), dir);

        var index = File.ReadAllText(Path.Combine(dir, "全部对话_按目录分组.md"));

        // Three rows, two conversations: "b" is the transcript behind the Desktop record.
        Assert.Contains("共 **2** 个不重复对话", index);
        Assert.Contains("**1** 个已经在 Claude 侧边栏里", index);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
