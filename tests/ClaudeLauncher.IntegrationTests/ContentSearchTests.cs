using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Search;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Searching what was said, not only titles: the index is fed from the transcripts, kept up
/// to date a piece at a time, and consulted by the same search box.
/// </summary>
public sealed class ContentSearchTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteCatalog _catalog;
    private readonly ContentIndex _index;

    public ContentSearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-content", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        _catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
        _index = new ContentIndex(Path.Combine(_dir, "search-index.sqlite"));
        _catalog.AttachContentIndex(_index.DatabasePath);
    }

    private static string User(string text) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private static string Assistant(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private static string ToolResult(string text) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"" + text + "\"}]}}";

    private string Transcript(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        return path;
    }

    private SessionMetadata Row(string id, string title, int minutesAgo = 0)
    {
        var row = new SessionMetadata
        {
            Key = new ConversationKey(ConversationKey.CliCode, "acc", "org", id),
            Title = title,
            SearchKey = SearchKeys.Normalize(title),
            SourceRevision = "1:1",
            LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            NativeArchiveState = NativeArchiveState.Unsupported,
            SourceCoverage = SourceCoverage.Resolved,
        };
        _catalog.UpsertRange([row]);
        return row;
    }

    private static ContentIndex.Source Source(SessionMetadata row, string path) => new(ContentIndex.KeyOf(row.Key), path);

    /// <summary>What the app does: update the index, then tell the catalog it changed.</summary>
    private ContentIndex.UpdateRun Update(IReadOnlyList<ContentIndex.Source> sources)
    {
        var run = _index.Update(sources);
        if (run.ChangedAnything) _catalog.ContentIndexChanged();
        return run;
    }

    private List<string> Found(string search) =>
        _catalog.Page(Visibility.Active, search, null, 100).Rows.Select(r => r.Key.NativeSessionId).ToList();

    [Fact]
    public void WhatWasSaidFindsItsConversation()
    {
        var a = Row("a", "GWAS 工具");
        var b = Row("b", "论文润色");
        var run = Update([
            Source(a, Transcript("a", User("KASP 引物怎么设计"), Assistant("先确定候选基因的位置"))),
            Source(b, Transcript("b", User("病级提取的表格"), Assistant("好的"))),
        ]);
        Assert.Equal(2, run.Indexed);

        Assert.Equal(["a"], Found("候选基因"));
        Assert.Equal(["a"], Found("kasp"));                  // case folded
        Assert.Equal(["b"], Found("病级"));                   // two characters: a scan, still found
        Assert.Empty(Found("KASP 病级"));                     // every word must be in the same conversation
        Assert.Equal(["a"], Found("GWAS 候选基因"));          // a title word and a content word together
    }

    [Fact]
    public void ASearchMadeBeforeTheIndexLearnedAWordFindsItAfterwards()
    {
        var a = Row("a", "会话");
        var path = Transcript("a", User("开头"));
        Update([Source(a, path)]);
        Assert.Empty(Found("后来才说的词"));            // looked up once and remembered for this search

        File.AppendAllText(path, User("后来才说的词") + "\n", new UTF8Encoding(false));
        Update([Source(a, path)]);
        Assert.Equal(["a"], Found("后来才说的词"));
    }

    [Fact]
    public void ARowSaysWhenOnlyWhatWasSaidMatched()
    {
        var row = Row("abc123def", "GWAS 工具") with { LocalNote = "备注里的词", ProjectRef = @"D:\wheat\项目" };
        string[] Terms(string s) => [.. SearchKeys.Terms(s)];

        Assert.Null(SqliteCatalog.ContentOnlyTerm(row, Terms("gwas")));          // title
        Assert.Null(SqliteCatalog.ContentOnlyTerm(row, Terms("备注里")));         // the user's note
        Assert.Null(SqliteCatalog.ContentOnlyTerm(row, Terms("wheat")));         // folder
        Assert.Null(SqliteCatalog.ContentOnlyTerm(row, Terms("abc123")));        // id
        Assert.Equal("候选基因", SqliteCatalog.ContentOnlyTerm(row, Terms("GWAS 候选基因")));
    }

    [Fact]
    public void AFullCacheNeverDropsATermOfTheSearchInHand()
    {
        var a = Row("a", "标题词在这里");
        Update([Source(a, Transcript("a", User("只在内容里的术语一")))]);
        Assert.Equal(["a"], Found("术语一"));                          // looked up and remembered

        for (var i = 0; i < 40; i++) Found($"填充词{i:00}");             // more searches than the cache keeps

        // One term remembered, one new: making room for the new one must not drop the other.
        Assert.Equal(["a"], Found("术语一 标题词"));
        Assert.Equal(1, _catalog.Count(Visibility.Active, "术语一 标题词"));
    }

    private List<string> Ranked(string search, bool reversed = false, bool mineOnly = false) =>
        _catalog.Page(Visibility.Active, search, null, 100, sort: new SortOrder(SortField.Relevance, reversed), mineOnly: mineOnly)
            .Rows.Select(r => r.Key.NativeSessionId).ToList();

    [Fact]
    public void AWordCanBeLeftOut()
    {
        var a = Row("a", "GWAS 工具");
        var b = Row("b", "GWAS 论文");
        Update([
            Source(a, Transcript("a", User("KASP 引物怎么设计"))),
            Source(b, Transcript("b", User("只是润色"))),
        ]);

        Assert.Equal(["b"], Found("gwas -kasp"));          // said in a
        Assert.Equal(["a"], Found("gwas -论文"));           // in b's title
        Assert.Equal(["b"], Found("-kasp"));               // only a word to leave out: everything else
        Assert.Equal(1, _catalog.Count(Visibility.Active, "gwas -kasp"));
    }

    [Fact]
    public void AQuotedPhraseMatchesOnlyWhole()
    {
        var a = Row("a", "会话一");
        var b = Row("b", "会话二");
        Update([
            Source(a, Transcript("a", User("KASP 引物怎么设计"))),
            Source(b, Transcript("b", User("引物 另说，KASP 也另说"))),
        ]);

        Assert.Equal(2, Found("kasp 引物").Count);         // both words, anywhere
        Assert.Equal(["a"], Found("\"kasp 引物\""));       // the phrase itself
    }

    [Fact]
    public void MostRelevantFirstThenTheMostMentions()
    {
        var named = Row("named", "KASP 引物设计", minutesAgo: 30);    // in its title
        var once = Row("once", "会话一", minutesAgo: 20);
        var often = Row("often", "会话二", minutesAgo: 10);
        Update([
            Source(named, Transcript("named", User("开始"))),
            Source(once, Transcript("once", User("KASP 一次"))),
            Source(often, Transcript("often", User("KASP 一"), Assistant("KASP 二"), User("KASP 三"))),
        ]);

        Assert.Equal(["often", "once", "named"], Found("kasp"));           // newest first, as usual
        Assert.Equal(["named", "often", "once"], Ranked("kasp"));
        Assert.Equal(["once", "often", "named"], Ranked("kasp", reversed: true));

        var terms = SearchKeys.Terms("kasp");
        var counts = _catalog.SectionCounts(Visibility.Active, "kasp", GroupFilter.All, BackendFilter.All, SortField.Relevance);
        Assert.Equal(1, counts[SqliteCatalog.RelevanceNamedKey]);
        Assert.Equal(2, counts[SqliteCatalog.RelevanceSaidKey]);
        Assert.Equal(SqliteCatalog.RelevanceNamedKey, SqliteCatalog.SectionKey(named, SortField.Relevance, terms: terms));
        Assert.Equal(SqliteCatalog.RelevanceSaidKey, SqliteCatalog.SectionKey(often, SortField.Relevance, terms: terms));

        var hits = _catalog.ContentHitCounts("kasp")["kasp"];
        Assert.Equal(3, hits[ContentIndex.KeyOf(often.Key)]);
        Assert.Equal(1, hits[ContentIndex.KeyOf(once.Key)]);
        Assert.False(hits.ContainsKey(ContentIndex.KeyOf(named.Key)));
    }

    [Fact]
    public void RelevancePagesNeitherRepeatNorSkip()
    {
        var rows = Enumerable.Range(0, 30).Select(i => Row($"r{i:00}", i % 3 == 0 ? "KASP 标题" : "会话")).ToList();
        Update([.. rows.Select((r, i) => Source(r, Transcript(r.Key.NativeSessionId,
            [.. Enumerable.Range(0, i % 5).Select(_ => User("KASP 引物"))])))]);

        var all = Ranked("kasp");
        var paged = new List<string>();
        PageCursor? cursor = null;
        do
        {
            var page = _catalog.Page(Visibility.Active, "kasp", cursor, 7, sort: new SortOrder(SortField.Relevance, false));
            paged.AddRange(page.Rows.Select(r => r.Key.NativeSessionId));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(all, paged);
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void OnlyWhatTheUserSaidWhenAsked()
    {
        var a = Row("a", "会话一");
        var b = Row("b", "会话二");
        Update([
            Source(a, Transcript("a", User("帮我设计 KASP 引物"))),
            Source(b, Transcript("b", User("接着做"), Assistant("可以用 KASP 引物"))),
        ]);

        Assert.Equal(2, Found("kasp").Count);
        Assert.Equal(["a"], Ranked("kasp", mineOnly: true));
        Assert.Equal(1, _catalog.Count(Visibility.Active, "kasp", mineOnly: true));
        Assert.Equal(2, _catalog.Count(Visibility.Active, "kasp"));      // the same word, both tables kept apart
        Assert.Single(_catalog.ContentHitCounts("kasp", mineOnly: true)["kasp"]);

        Assert.Single(_index.Hits(ContentIndex.KeyOf(b.Key), "kasp", 3));
        Assert.Empty(_index.Hits(ContentIndex.KeyOf(b.Key), "kasp", 3, userOnly: true));
    }

    [Fact]
    public void ADamagedIndexFileIsStartedOver()
    {
        var path = Path.Combine(_dir, "damaged-index.sqlite");
        File.WriteAllBytes(path, [.. Enumerable.Range(0, 8192).Select(i => (byte)(i * 7))]);

        using var index = ContentIndex.OpenOrRebuild(path);
        Assert.NotNull(index);
        var a = Row("a", "会话");
        Assert.Equal(1, index.Update([Source(a, Transcript("a", User("重建之后能用")))]).Indexed);
    }

    [Fact]
    public void SwitchedOffTheIndexIsNoLongerConsulted()
    {
        var a = Row("a", "标题");
        Update([Source(a, Transcript("a", User("只在内容里说过的词")))]);
        Assert.Equal(["a"], Found("说过的词"));

        _catalog.DetachContentIndex();
        Assert.Empty(Found("说过的词"));                    // off: titles, folders and ids only
        Assert.Equal(["a"], Found("标题"));

        _catalog.AttachContentIndex(_index.DatabasePath);
        Assert.Equal(["a"], Found("说过的词"));             // on again
    }

    [Fact]
    public void ToolOutputIsNotSearched()
    {
        var a = Row("a", "工具");
        Update([Source(a, Transcript("a", User("跑一下"), ToolResult("SECRETTOOLOUTPUT 十万行")))]);

        Assert.Empty(Found("SECRETTOOLOUTPUT"));
    }

    [Fact]
    public void LongLinesAreJudgedByWhatTheyAre()
    {
        var a = Row("a", "长行");
        var bulk = new string('x', 100_000);
        Update([Source(a, Transcript("a",
            ToolResult("TOOLNEEDLE " + bulk),
            User("长消息开头 " + new string('y', 100_000)),
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"说明文字 TEXTFIRST\"},"
            + "{\"type\":\"tool_use\",\"name\":\"Write\",\"input\":{\"content\":\"" + bulk + "\"}}]}}"))]);

        Assert.Empty(Found("TOOLNEEDLE"));                 // a long tool output: passed over unread
        Assert.Equal(["a"], Found("长消息开头"));            // a long message: kept, cut to size
        Assert.Equal(["a"], Found("TEXTFIRST"));           // text ahead of a long tool call: kept
    }

    [Fact]
    public void OneLineBufferServesAWholePassWithoutCarryingAnythingOver()
    {
        // One buffer is lent to every transcript of a pass. The first ends in a line still being
        // written, and holds a long message before it; none of that may reach the next one.
        var a = Row("a", "甲");
        var b = Row("b", "乙");
        var first = Path.Combine(_dir, "a.jsonl");
        File.WriteAllText(first, User("甲的长消息 " + new string('y', 300_000)) + "\n"
            + "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"写到一半的UNFINISHED", new UTF8Encoding(false));
        var run = Update([Source(a, first), Source(b, Transcript("b", User("乙的第一句话"), Assistant("乙的回答")))]);

        Assert.Equal(2, run.Indexed);
        Assert.Equal(["a"], Found("甲的长消息"));
        Assert.Equal(["b"], Found("乙的第一句话"));
        Assert.Empty(Found("UNFINISHED"));                  // read once it is finished, not before
    }

    [Fact]
    public void AnIndexOfAnotherLayoutStartsOver()
    {
        var path = Path.Combine(_dir, "old-index.sqlite");
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = "CREATE TABLE sources(conversation TEXT PRIMARY KEY, path TEXT); PRAGMA user_version=1;";
            cmd.ExecuteNonQuery();
        }

        using var index = new ContentIndex(path);
        var a = Row("a", "会话");
        Assert.Equal(1, index.Update([Source(a, Transcript("a", User("重建后的内容")))]).Indexed);
        Assert.Single(index.Hits(ContentIndex.KeyOf(a.Key), "重建后"));
    }

    [Fact]
    public void AGrowingTranscriptIsReadOnFromWhereItStopped()
    {
        var a = Row("a", "进行中的会话");
        var path = Transcript("a", User("第一句话"));
        Update([Source(a, path)]);

        File.AppendAllText(path, Assistant("新加的回答里有关键词") + "\n", new UTF8Encoding(false));
        var run = Update([Source(a, path)]);
        Assert.Equal(1, run.Indexed);
        Assert.Equal(1, run.Messages);                        // only the new line was read
        Assert.Equal(["a"], Found("关键词"));
        Assert.Single(_index.Hits(ContentIndex.KeyOf(a.Key), "第一句"));   // not indexed twice

        // Nothing changed: nothing read.
        Assert.Equal(0, Update([Source(a, path)]).Indexed);
    }

    [Fact]
    public void ReadingWhatHoldsNothingNewToFindIsNoChange()
    {
        // An agent at work: its transcript grows by tool calls and their output, nothing said.
        // Reported as a change, every search on screen would be worked out again after each sync.
        var a = Row("a", "进行中的会话");
        var path = Transcript("a", User("第一句话"), ToolResult("工具的输出"), Assistant("回答"));
        Assert.True(Update([Source(a, path)]).ChangedAnything);

        File.AppendAllText(path, ToolResult("又一段工具输出") + "\n", new UTF8Encoding(false));
        var grown = Update([Source(a, path)]);
        Assert.Equal(1, grown.Indexed);                        // read on from where it stopped…
        Assert.False(grown.ChangedAnything);                   // …and nothing in it to find

        // Written again, shorter, with the same words: read from the start, the rows stand.
        Transcript("a", User("第一句话"), Assistant("回答"));
        var rewritten = Update([Source(a, path)]);
        Assert.Equal(1, rewritten.Indexed);
        Assert.False(rewritten.ChangedAnything);
        Assert.Single(_index.Hits(ContentIndex.KeyOf(a.Key), "第一句"));

        // Written again with other words: that is a change.
        Transcript("a", User("换了说法"));
        Assert.True(Update([Source(a, path)]).ChangedAnything);
        Assert.Equal(["a"], Found("换了说法"));
        Assert.Empty(Found("第一句话"));

        // And emptied: what was said is gone, which is a change too.
        Transcript("a");
        Assert.True(Update([Source(a, path)]).ChangedAnything);
        Assert.Empty(Found("换了说法"));
    }

    [Fact]
    public void ARewrittenTranscriptStartsOver()
    {
        var a = Row("a", "会话");
        var path = Transcript("a", User("很长的旧内容 旧词"), Assistant("更多旧内容"));
        Update([Source(a, path)]);

        Transcript("a", User("新词"));                          // shorter: rewritten, not grown
        Update([Source(a, path)]);

        Assert.Empty(Found("旧词"));
        Assert.Equal(["a"], Found("新词"));
    }

    [Fact]
    public void AConversationNoLongerListedLeavesTheIndex()
    {
        var a = Row("a", "甲");
        var b = Row("b", "乙");
        Update([Source(a, Transcript("a", User("甲的独有内容"))), Source(b, Transcript("b", User("乙的独有内容")))]);

        var run = Update([Source(a, Path.Combine(_dir, "a.jsonl"))]);
        Assert.Equal(1, run.Removed);
        Assert.Empty(_index.Hits(ContentIndex.KeyOf(b.Key), "乙的独有"));
        Assert.Equal(1, _index.Count());
    }

    [Fact]
    public void AHitShowsTheWordsAroundIt()
    {
        var a = Row("a", "会话");
        var filler = new string('前', 60);
        Update([Source(a, Transcript("a", User(filler + "候选基因列表" + new string('后', 200))))]);

        var hit = Assert.Single(_index.Hits(ContentIndex.KeyOf(a.Key), "候选基因"));
        Assert.True(hit.ByUser);
        Assert.Contains("候选基因", hit.Snippet, StringComparison.Ordinal);
        Assert.StartsWith("…", hit.Snippet, StringComparison.Ordinal);
        Assert.EndsWith("…", hit.Snippet, StringComparison.Ordinal);
        Assert.True(hit.Snippet.Length < 150);
    }

    [Theory]
    [InlineData(ConversationKey.CliCode, "local", "default", "3f2a9c10-aaaa-4bbb-8ccc-000000000001")]
    [InlineData(ConversationKey.Codex, @"C:\Users\someone\.codex", "default", "019a2b3c-0000-7000-8000-000000000001")]
    [InlineData(ConversationKey.DeepSeek, @"D:\Tools\DSH Home", "", "sess_01")]
    [InlineData(ConversationKey.ChatGptWeb, "", "", "id|with|bars")]
    public void ANameInTheIndexLeadsBackToItsKey(string backend, string account, string org, string id)
    {
        var key = new ConversationKey(backend, account, org, id);
        Assert.True(ContentIndex.TryParseKey(ContentIndex.KeyOf(key), out var parsed));
        Assert.Equal(key, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cli-code|local|default")]
    [InlineData("|local|default|abc")]
    [InlineData("cli-code|local|default|")]
    public void WhatNoKeyNamesIsNotTakenForOne(string conversation)
    {
        Assert.False(ContentIndex.TryParseKey(conversation, out var key));
        Assert.Null(key);
    }

    public void Dispose()
    {
        _catalog.Dispose();
        _index.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
