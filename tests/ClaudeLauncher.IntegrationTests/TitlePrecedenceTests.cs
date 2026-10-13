using System.Text;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Claude Code writes the title it already computed into the transcript. Inferring one from
/// message text is only a last resort — it fails on /compact continuations, whose opening
/// messages are entirely boilerplate.
/// </summary>
public sealed class TitlePrecedenceTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _projects;

    public TitlePrecedenceTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "launcher-titles", Guid.NewGuid().ToString("n"));
        _projects = Path.Combine(_configDir, "projects");
        Directory.CreateDirectory(_projects);
    }

    private string Write(string name, params string[] lines)
    {
        var dir = Path.Combine(_projects, "G--demo");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        return path;
    }

    private string WriteIn(string project, string name, DateTime writtenUtc, params string[] lines)
    {
        var dir = Path.Combine(_projects, project);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private static string User(string text) =>
        "{\"type\":\"user\",\"cwd\":\"G:\\\\demo\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\""
        + text + "\"}]}}";

    private static string Custom(string t) =>
        "{\"type\":\"custom-title\",\"customTitle\":\"" + t + "\",\"cwd\":\"G:\\\\demo\"}";

    private static string Ai(string t) =>
        "{\"type\":\"ai-title\",\"aiTitle\":\"" + t + "\",\"cwd\":\"G:\\\\demo\"}";

    private IReadOnlyList<Core.Contracts.SessionMetadata> Read() =>
        new CliMetadataReader(_configDir).Read("acc", "org").ToList();

    [Fact]
    public void UserTitleWinsOverEverythingElse()
    {
        Write("11111111-0000-0000-0000-000000000001",
            Custom("我起的标题"), Ai("An AI title"), User("第一条真实提问"));

        Assert.Equal("我起的标题", Read().Single().Title);
    }

    [Fact]
    public void InjectedTextIsNeverTakenForTheTitle()
    {
        // A session started through a skill: the skill's text comes first, marked isMeta.
        var injected = "{\"type\":\"user\",\"isMeta\":true,\"cwd\":\"G:\\\\demo\",\"message\":{\"content\":"
            + "[{\"type\":\"text\",\"text\":\"Workflow authoring reference for skills\"}]}}";
        Write("33333333-0000-0000-0000-000000000003", injected, User("整理小麦病级数据"));

        Assert.Equal("整理小麦病级数据", Read().Single().Title);
    }

    [Fact]
    public void TheWordsAfterTheDesktopAppsReminderAreTheTitle()
    {
        // A session started in Claude's desktop app without a project folder.
        Write("dddddddd-0000-0000-0000-00000000000d",
            User("<system-reminder>\\nThe user started this session without choosing a project folder.\\n</system-reminder>\\n\\n整理小麦病级数据"));

        var row = Read().Single();
        Assert.Equal("整理小麦病级数据", row.Title);
        Assert.False(row.TitleIsPlaceholder);
    }

    [Fact]
    public void AMentionedFileIsNamedNotSpelledOut()
    {
        Write("eeeeeeee-0000-0000-0000-00000000000e",
            User("@\\\"G:\\\\论文\\\\撰写\\\\润色稿(3).docx\\\" 按期刊要求润色"));

        Assert.Equal("润色稿(3).docx 按期刊要求润色", Read().Single().Title);
    }

    [Fact]
    public void ABrokenSurrogateStillMakesATitle()
    {
        Write("cccccccc-0000-0000-0000-00000000000c", User("整理表情包\\ud83d的数据"));

        Assert.Equal("整理表情包�的数据", Read().Single().Title);
    }

    [Fact]
    public void SlashCommandArgumentsBecomeTheTitle()
    {
        // "/loop 规划…" is recorded as markup; the request inside it is the conversation's topic.
        Write("99999999-0000-0000-0000-000000000009",
            User("<command-message>loop</command-message> <command-name>/loop</command-name> "
                 + "<command-args>规划做一个道德与法治的 AI 交互平台</command-args>"));

        Assert.Equal("规划做一个道德与法治的 AI 交互平台", Read().Single().Title);
    }

    [Fact]
    public void SettingsCommandIsNotTakenForTheTitle()
    {
        Write("aaaaaaaa-0000-0000-0000-00000000000a",
            User("<command-name>/model</command-name> <command-args>claude-fable-5-1</command-args>"),
            User("<command-message>init</command-message> <command-name>/init</command-name>"),
            User("整理小麦病级数据"));

        Assert.Equal("整理小麦病级数据", Read().Single().Title);
    }

    [Fact]
    public void AMovedProjectsNewestCopyIsTheConversation()
    {
        // The project moved from D: to G:, and Claude Code's transcript was copied along.
        const string id = "bbbbbbbb-0000-0000-0000-00000000000b";
        var old = WriteIn("D--wheat", id, new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
            Custom("旧的标题"), User("第一条真实提问"));
        var current = WriteIn("G--wheat", id, new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc),
            Custom("新的标题"), User("第一条真实提问"));

        var row = Read().Single();
        Assert.Equal("新的标题", row.Title);
        Assert.Equal(new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc), row.LastActivityUtc?.UtcDateTime);
        // The reader opens the same copy the row was read from, whichever folder sorts first.
        Assert.Equal(current, TranscriptLocator.Find(row, _configDir));
        Assert.NotEqual(old, current);
    }

    [Fact]
    public void AiTitleWinsOverTheFirstMessage()
    {
        Write("22222222-0000-0000-0000-000000000002",
            Ai("An AI title"), User("第一条真实提问"));

        Assert.Equal("An AI title", Read().Single().Title);
    }

    [Fact]
    public void FirstMessageIsUsedWhenNoTitleRecordExists()
    {
        Write("33333333-0000-0000-0000-000000000003", User("第一条真实提问"));

        Assert.Equal("第一条真实提问", Read().Single().Title);
    }

    [Fact]
    public void CompactContinuationStillGetsATitle()
    {
        // The real-world failure: every opening message is boilerplate, so message-based
        // inference produces nothing — but the title record is right there on line 1.
        Write("44444444-0000-0000-0000-000000000004",
            Custom("综合分析流程"),
            User("This session is being continued from a previous conversation that ran out of context."),
            User("<local-command-caveat>Caveat: The messages below were generated by the user"),
            User("<command-name>/compact</command-name>"));

        var row = Read().Single();
        Assert.Equal("综合分析流程", row.Title);
        Assert.False(row.TitleIsPlaceholder);
    }

    [Fact]
    public void ABoilerplateOnlyTranscriptWithNoTitleRecordStaysAPlaceholder()
    {
        // Honest fallback: we must not invent a title from boilerplate.
        Write("55555555-0000-0000-0000-000000000005",
            User("This session is being continued from a previous conversation that ran out of context."),
            User("<command-name>/compact</command-name>"));

        var row = Read().Single();
        Assert.True(row.TitleIsPlaceholder);
        Assert.Equal("55555555-0000-0000-0000-000000000005", row.Title);
    }

    [Fact]
    public void ShortChineseTitleIsKept()
    {
        // 4 CJK characters is a complete title; an ASCII character-count floor would drop it.
        Write("77777777-0000-0000-0000-000000000007", User("病级提取"));

        var row = Read().Single();
        Assert.Equal("病级提取", row.Title);
        Assert.False(row.TitleIsPlaceholder);
    }

    [Fact]
    public void TooShortToMeanAnythingStaysAPlaceholder()
    {
        Write("88888888-0000-0000-0000-000000000008", User("ok"));

        Assert.True(Read().Single().TitleIsPlaceholder);
    }

    [Fact]
    public void BlankTitleRecordIsIgnored()
    {
        Write("66666666-0000-0000-0000-000000000006",
            Custom("   "), User("真实提问"));

        Assert.Equal("真实提问", Read().Single().Title);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { /* temp dir */ }
    }
}
