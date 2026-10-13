using System.Text;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The extractor quotes; it must never summarise. These tests mostly pin down that boundary
/// and the refusal to mine Claude's own words.
/// </summary>
public sealed class MemoryCandidateTests : IDisposable
{
    private readonly string _dir;

    public MemoryCandidateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-memory", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        return path;
    }

    private static string User(string t) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + t + "\"}]}}";

    private static string Assistant(string t) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + t + "\"}]}}";

    private MemoryDraft Extract(string path) =>
        MemoryCandidateExtractor.Extract(path, "local_x", "测试会话", @"G:\demo", DateTimeOffset.UtcNow);

    [Fact]
    public void StandingInstructionIsCaptured()
    {
        var d = Extract(Write(User("以后一律把临时文件放 D 盘")));

        var c = Assert.Single(d.Candidates);
        Assert.Equal(CandidateKind.Instruction, c.Kind);
        // Verbatim: the user's sentence, not a paraphrase of it.
        Assert.Equal("以后一律把临时文件放 D 盘", c.Quote);
    }

    [Fact]
    public void CorrectionOutranksOtherShapes()
    {
        // "不对" and "改成" both match; a correction is the more useful classification.
        var d = Extract(Write(User("不对，应该是改成 UTF-8 带 BOM")));

        Assert.Equal(CandidateKind.Correction, Assert.Single(d.Candidates).Kind);
    }

    [Fact]
    public void DecisionAndOpenThreadAreSeparated()
    {
        var d = Extract(Write(
            User("就用 .NET 10 WPF 吧"),
            User("测试还没写完，下一步再补")));

        Assert.Contains(d.Candidates, c => c.Kind == CandidateKind.Decision);
        Assert.Contains(d.Candidates, c => c.Kind == CandidateKind.OpenThread);
    }

    [Fact]
    public void ClaudesOwnWordsAreNeverMined()
    {
        // Filing Claude's phrasing back as the user's intent is how a misreading becomes
        // permanent, so assistant messages are excluded even when they match perfectly.
        var d = Extract(Write(Assistant("以后一律用这个方案"), Assistant("决定改成 B")));

        Assert.Empty(d.Candidates);
        Assert.Equal(0, d.UserMessagesScanned);
    }

    [Fact]
    public void BoilerplateIsNotScanned()
    {
        var d = Extract(Write(
            User("<command-name>/compact</command-name> 以后一律这样"),
            User("This session is being continued from a previous conversation 决定用 X")));

        Assert.Empty(d.Candidates);
    }

    [Fact]
    public void SlashCommandArgumentsAreNotMined()
    {
        // Shown as what the user typed, but "/loop …" hands a task to a command.
        var d = Extract(Write(User("<command-name>/loop</command-name><command-args>以后一律这样做</command-args>")));

        Assert.Empty(d.Candidates);
    }

    [Fact]
    public void PastedSkillDefinitionIsNotMinedAsUserIntent()
    {
        // When a skill runs, its whole definition arrives as a user message. Its rules are
        // the skill's, not yours — filing them as your instructions would be a fabrication.
        var skill = "Base directory for this skill: C:/skills/demo. 以后一律使用 TDD。"
                    + new string('x', 2000);
        var d = Extract(Write(User(skill)));

        Assert.Empty(d.Candidates);
        Assert.Equal(0, d.UserMessagesScanned);
        Assert.Equal(1, d.PastedBlocksSkipped);
    }

    [Fact]
    public void LongPastedDocumentIsSkipped()
    {
        var d = Extract(Write(User("以后一律这样做。" + new string('文', 2000))));

        Assert.Empty(d.Candidates);
        Assert.Equal(1, d.PastedBlocksSkipped);
    }

    [Fact]
    public void ShortTypedInstructionStillCounts()
    {
        // The exclusion must not swallow the real thing.
        var d = Extract(Write(User("以后一律把临时文件放 D 盘")));

        Assert.Single(d.Candidates);
        Assert.Equal(0, d.PastedBlocksSkipped);
    }

    [Fact]
    public void OrdinaryChatProducesNothing()
    {
        var d = Extract(Write(User("这个文件在哪里？"), User("帮我看看这段代码")));

        Assert.Empty(d.Candidates);
        Assert.Equal(2, d.UserMessagesScanned);
    }

    [Fact]
    public void RepeatedInstructionIsNotDuplicated()
    {
        var d = Extract(Write(
            User("以后一律放 D 盘"), User("以后一律放 D 盘"), User("以后一律放 D 盘")));

        Assert.Single(d.Candidates);
    }

    [Fact]
    public void CandidateCountIsCapped()
    {
        var many = Enumerable.Range(0, 200).Select(i => User($"以后一律执行规则{i}")).ToArray();

        Assert.Equal(MemoryCandidateExtractor.MaxCandidates, Extract(Write(many)).Candidates.Count);
    }

    [Fact]
    public void RenderedDraftSaysItIsNotAFact()
    {
        var text = MemoryCandidateExtractor.Render(Extract(Write(User("以后一律放 D 盘"))));

        // The header is the whole safety story: a draft must not read like settled memory.
        Assert.Contains("候选草稿，不是事实", text, StringComparison.Ordinal);
        Assert.Contains("原样摘录", text, StringComparison.Ordinal);
        Assert.Contains("- [ ] ", text, StringComparison.Ordinal);   // unchecked, needs review
        Assert.StartsWith("---", text, StringComparison.Ordinal);    // memory front-matter shape
        Assert.Contains("type: project", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyResultSaysSoWithoutImplyingTheSessionWasWorthless()
    {
        var text = MemoryCandidateExtractor.Render(Extract(Write(User("随便聊聊"))));

        Assert.Contains("不代表这段对话没有价值", text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
