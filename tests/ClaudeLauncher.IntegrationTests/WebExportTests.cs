using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Discovery;
using ClaudeLauncher.Infrastructure.Launch;
using ClaudeLauncher.Infrastructure.Search;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// ChatGPT's and Claude's conversations on the web, from the data exports they mail: the branch
/// that was last seen and nothing of the others, thinking and tool calls folded beside the
/// answer, pictures kept from the export or named when it has none, numbered conversation
/// files read like one, the wrong export recognised; a later import adds and updates but never
/// removes; and the list, the reader, the search and the links take it in.
/// </summary>
public sealed class WebExportTests : IDisposable
{
    private const string ConvA = "1f0e3c4d-0000-4000-8000-0000000000a1";
    private const string ConvB = "1f0e3c4d-0000-4000-8000-0000000000b2";
    private const string ConvC = "2a3b4c5d-0000-4000-8000-0000000000c1";
    private const string ConvD = "2a3b4c5d-0000-4000-8000-0000000000d2";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Webp = [0x52, 0x49, 0x46, 0x46, 4, 0, 0, 0, 0x57, 0x45, 0x42, 0x50];

    private readonly string _dir;
    private readonly WebExportStore _chatGpt;
    private readonly WebExportStore _claude;

    public WebExportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-webexport", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _chatGpt = new WebExportStore(ConversationKey.ChatGptWeb, Path.Combine(_dir, "data", "chatgpt-web"));
        _claude = new WebExportStore(ConversationKey.ClaudeWeb, Path.Combine(_dir, "data", "claude-web"));
    }

    // ------------------------------------------------------------------ ChatGPT's export

    private static object Node(string id, string? parent, string[] children, object? message) =>
        new { id, parent, children, message };

    private static object Message(string role, object content, string? recipient = "all", object? metadata = null,
        double? time = 1760000100, string? name = null) => new
    {
        author = new { role, name, metadata = new { } },
        create_time = time,
        content,
        status = "finished_successfully",
        recipient,
        metadata = metadata ?? new { },
    };

    private static object Text(params string[] parts) => new { content_type = "text", parts };

    /// <summary>A conversation as ChatGPT exports it: a tree, an edited question's old answer a branch beside the one shown.</summary>
    private static object ConversationA(string answer = "这是引物citeturn0search1：ATGC【4†source】") => new
    {
        title = "引物设计",
        create_time = 1760000000.5,
        update_time = 1760000600.0,
        mapping = new Dictionary<string, object>
        {
            ["root"] = Node("root", null, ["sys"], null),
            ["sys"] = Node("sys", "root", ["u1"], Message("system", Text(""),
                metadata: new { is_visually_hidden_from_conversation = true })),
            ["u1"] = Node("u1", "sys", ["old", "a1"], Message("user", new
            {
                content_type = "multimodal_text",
                parts = new object[]
                {
                    new { content_type = "image_asset_pointer", asset_pointer = "file-service://file-AbC123", size_bytes = 11 },
                    "帮我设计一对引物",
                },
            }, metadata: new
            {
                attachments = new object[]
                {
                    new { id = "file-AbC123", name = "gel.png", mime_type = "image/png" },
                    new { id = "file-Missing9", name = "notes.pdf", mime_type = "application/pdf" },
                },
            })),
            ["old"] = Node("old", "u1", [], Message("assistant", Text("被重新生成掉的旧回答"))),
            ["a1"] = Node("a1", "u1", ["a2"], Message("assistant", new
            {
                content_type = "thoughts",
                thoughts = new[] { new { summary = "看序列", content = "先看 GC 含量" } },
            })),
            ["a2"] = Node("a2", "a1", ["t1"], Message("assistant", new { content_type = "code", language = "unknown", text = "print(1)" },
                recipient: "python")),
            ["t1"] = Node("t1", "a2", ["a3"], Message("tool", new { content_type = "execution_output", text = "1" }, name: "python")),
            ["a3"] = Node("a3", "t1", [], Message("assistant", Text(answer))),
        },
        current_node = "a3",
        conversation_id = ConvA,
        id = ConvA,
    };

    /// <summary>No current node, no title; a picture made by a tool.</summary>
    private static object ConversationB() => new
    {
        title = (string?)null,
        create_time = 1760100000.0,
        update_time = 1760100500.0,
        mapping = new Dictionary<string, object>
        {
            ["r"] = Node("r", null, ["u"], null),
            ["u"] = Node("u", "r", ["c"], Message("user", Text("画一只猫"), time: 1760100010)),
            ["c"] = Node("c", "u", ["t"], Message("assistant", Text("{\"prompt\":\"a cat\"}"), recipient: "dalle.text2im",
                time: 1760100020)),
            ["t"] = Node("t", "c", ["d"], Message("tool", new
            {
                content_type = "multimodal_text",
                parts = new object[] { new { content_type = "image_asset_pointer", asset_pointer = "sediment://file_00000000feed" } },
            }, name: "dalle.text2im", time: 1760100030)),
            ["d"] = Node("d", "t", [], Message("assistant", Text("画好了"), time: 1760100040)),
        },
        current_node = (string?)null,
        id = ConvB,
    };

    private string Zip(string name, Dictionary<string, object> files)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in files)
            using (var stream = zip.CreateEntry(entry).Open())
                stream.Write(content is byte[] bytes ? bytes : JsonSerializer.SerializeToUtf8Bytes(content));
        return path;
    }

    /// <summary>A large export: the conversations split over numbered files.</summary>
    private string ChatGptExport(object? a = null) => Zip($"chatgpt-{Guid.NewGuid():n}.zip", new()
    {
        ["conversations-000.json"] = new[] { a ?? ConversationA() },
        ["conversations-001.json"] = new[] { ConversationB() },
        ["file-AbC123-gel.png"] = Png,
        ["dalle-generations/file_00000000feed-7b1c.webp"] = Webp,
        ["user.json"] = new { id = "user-x" },
        ["chat.html"] = Encoding.UTF8.GetBytes("<html></html>"),
    });

    private static string Words(string path) =>
        string.Join("\n", GeminiSession.ReadEntries(path, keepImages: true).Select(e => $"{e.Role}: {e.Text}"));

    [Fact]
    public void ChatGptsExportIsReadAsTheBranchLastSeenWithItsThinkingToolsAndPictures()
    {
        var export = ChatGptExport();
        var before = File.ReadAllBytes(export);

        var run = _chatGpt.Import([export]);

        Assert.Equal((2, 2, 0, 2, 0, 2, false), (run.Found, run.Added, run.Updated, run.Total, run.Unreadable, run.ConversationFiles, run.OtherKind));
        Assert.Equal(before, File.ReadAllBytes(export));                    // the export only read

        var a = Path.Combine(_chatGpt.ConversationsFolder, $"session-{ConvA}.json");
        var entries = GeminiSession.ReadEntries(a, keepImages: true);
        var shown = Words(a);
        Assert.DoesNotContain("被重新生成掉的旧回答", shown);              // the branch left behind
        Assert.DoesNotContain("", shown);
        Assert.DoesNotContain("†source", shown);
        Assert.Contains(entries, e => e is { Role: MessageRole.User, Text: "帮我设计一对引物" });
        var gel = Assert.Single(entries, e => e.Role == MessageRole.Attachment && e.Text.Contains("gel.png"));
        Assert.Equal(Png, gel.Image);                                       // kept from the export, shown
        Assert.Contains(entries, e => e.Role == MessageRole.Attachment && e.Text.Contains("notes.pdf") && e.Image is null);
        Assert.Contains(entries, e => e is { Role: MessageRole.Note } && e.Text.Contains("看序列"));
        Assert.Contains(entries, e => e is { Role: MessageRole.ToolCall } && e.Text.StartsWith("python", StringComparison.Ordinal));
        Assert.Contains(entries, e => e is { Role: MessageRole.ToolResult, Detail: "1" });
        Assert.Equal("这是引物：ATGC", Assert.Single(entries, e => e.Role == MessageRole.Assistant).Text);
        // In the order it was said: the question, the thinking, the tool, the answer.
        var order = entries.Select(e => e.Role).Where(r => r is not MessageRole.Attachment).ToList();
        Assert.Equal([MessageRole.User, MessageRole.Note, MessageRole.ToolCall, MessageRole.ToolResult, MessageRole.Assistant], order);

        var header = GeminiSession.ReadHeader(a)!;
        Assert.Equal("引物设计", header.Summary);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760000600000), header.LastUpdated);

        // Without a current node, the latest answer; a picture a tool made is the answer's.
        var b = GeminiSession.ReadEntries(Path.Combine(_chatGpt.ConversationsFolder, $"session-{ConvB}.json"), keepImages: true);
        Assert.Contains(b, e => e is { Role: MessageRole.Assistant, Text: "画好了" });
        Assert.Contains(b, e => e.Role == MessageRole.Attachment && e.Image is { } image && image.SequenceEqual(Webp));
        Assert.Contains(b, e => e is { Role: MessageRole.ToolCall } && e.Text.StartsWith("dalle.text2im", StringComparison.Ordinal));
    }

    [Fact]
    public void ALaterExportAddsAndUpdatesButNeverTakesAwayAndTheSameOneChangesNothing()
    {
        _chatGpt.Import([ChatGptExport()]);
        var a = Path.Combine(_chatGpt.ConversationsFolder, $"session-{ConvA}.json");
        var written = File.GetLastWriteTimeUtc(a);

        var again = _chatGpt.Import([ChatGptExport()]);
        Assert.Equal((2, 0, 0, 2), (again.Found, again.Added, again.Updated, again.Total));
        Assert.Equal(written, File.GetLastWriteTimeUtc(a));                 // not rewritten: nothing that reads it sees a change

        // A later export: A carried on, B deleted on the web since.
        var later = Zip("later.zip", new() { ["conversations.json"] = new[] { ConversationA("后来改过的回答") } });
        var run = _chatGpt.Import([later]);
        Assert.Equal((1, 0, 1, 2), (run.Found, run.Added, run.Updated, run.Total));
        Assert.Contains("后来改过的回答", Words(a));
        Assert.True(File.Exists(Path.Combine(_chatGpt.ConversationsFolder, $"session-{ConvB}.json")));   // kept

        Assert.Equal(2, _chatGpt.Describe().Conversations);
        Assert.Equal(2, _chatGpt.Remove());
        Assert.False(_chatGpt.Exists);
    }

    [Fact]
    public void AnUnpackedExportIsReadFromItsFolderAndTheWrongAppsExportIsNamed()
    {
        var folder = Path.Combine(_dir, "unpacked");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "conversations.json"), JsonSerializer.SerializeToUtf8Bytes(new[] { ConversationA() }));
        File.WriteAllBytes(Path.Combine(folder, "file-AbC123-gel.png"), Png);
        var run = _chatGpt.Import([Path.Combine(folder, "conversations.json")]);
        Assert.Equal((1, 1), (run.Found, run.Added));
        Assert.Contains(GeminiSession.ReadEntries(Path.Combine(_chatGpt.ConversationsFolder, $"session-{ConvA}.json"), keepImages: true),
            e => e.Image is { } image && image.SequenceEqual(Png));

        var wrong = _claude.Import([Path.Combine(folder, "conversations.json")]);
        Assert.Equal((0, true), (wrong.Found, wrong.OtherKind));
        Assert.False(_claude.Exists);

        var nothing = _chatGpt.Import([Zip("photos.zip", new() { ["a.png"] = Png })]);
        Assert.Equal((0, 0), (nothing.Found, nothing.ConversationFiles));
    }

    // ------------------------------------------------------------------- Claude's export

    private static object ClaudeMessage(string uuid, string sender, string at, object[] content, string? parent = null,
        object[]? attachments = null, object[]? files = null) => new
    {
        uuid,
        text = "",
        content,
        sender,
        created_at = at,
        updated_at = at,
        attachments = attachments ?? [],
        files = files ?? [],
        parent_message_uuid = parent,
    };

    private static object Block(string text) => new { type = "text", text };

    /// <summary>An edited question: the branch written last is the conversation.</summary>
    private static object ConversationC() => new
    {
        uuid = ConvC,
        name = "论文结构",
        created_at = "2026-09-01T08:00:00.000000Z",
        updated_at = "2026-09-01T09:00:00.000000Z",
        chat_messages = new[]
        {
            ClaudeMessage("m1", "human", "2026-09-01T08:00:00Z", [Block("帮我列个提纲")], "00000000-0000-4000-8000-000000000000",
                attachments: [new { file_name = "draft.docx", file_size = 10, file_type = "docx", extracted_content = "第一章 引言" }],
                files: [new { file_name = "photo.jpg" }]),
            ClaudeMessage("m2", "assistant", "2026-09-01T08:01:00Z",
            [
                new { type = "thinking", thinking = "想一想怎么分章", summaries = new[] { new { summary = "规划结构" } } },
                Block("好的"),
                new { type = "tool_use", id = "tu1", name = "artifacts", input = new { title = "提纲" } },
                new { type = "tool_result", tool_use_id = "tu1", name = "artifacts", content = new[] { Block("OK") } },
                Block("提纲如下"),
            ], "m1"),
            ClaudeMessage("m3", "human", "2026-09-01T08:02:00Z", [Block("改一下（被编辑掉的提问）")], "m2"),
            ClaudeMessage("m3b", "human", "2026-09-01T08:03:00Z", [Block("改成三章")], "m2"),
            ClaudeMessage("m4", "assistant", "2026-09-01T08:04:00Z", [Block("改好了")], "m3b"),
        },
    };

    /// <summary>An older export: no parent links, no name.</summary>
    private static object ConversationD() => new
    {
        uuid = ConvD,
        name = "",
        created_at = "2026-08-01T08:00:00Z",
        updated_at = "2026-08-01T08:10:00Z",
        chat_messages = new object[]
        {
            new { uuid = "x1", text = "第一句话", sender = "human", created_at = "2026-08-01T08:00:00Z" },
            new { uuid = "x2", text = "第一个回答", sender = "assistant", created_at = "2026-08-01T08:00:05Z" },
        },
    };

    [Fact]
    public void ClaudesExportIsReadAsTheBranchWrittenLastWithItsThinkingToolsAndAttachedText()
    {
        var export = Zip("claude.zip", new()
        {
            ["conversations.json"] = new[] { ConversationC(), ConversationD() },
            ["projects.json"] = Array.Empty<object>(),
            ["users.json"] = new[] { new { uuid = "u" } },
        });

        var run = _claude.Import([export]);
        Assert.Equal((2, 2, 0), (run.Found, run.Added, run.Unreadable));

        var c = Path.Combine(_claude.ConversationsFolder, $"session-{ConvC}.json");
        var entries = GeminiSession.ReadEntries(c);
        var shown = Words(c);
        Assert.DoesNotContain("被编辑掉的提问", shown);
        Assert.Equal(["帮我列个提纲", "改成三章"], entries.Where(e => e.Role == MessageRole.User).Select(e => e.Text));
        Assert.Equal(["好的", "提纲如下", "改好了"],
            entries.Where(e => e.Role == MessageRole.Assistant).Select(e => e.Text));
        Assert.Contains(entries, e => e is { Role: MessageRole.Note } && e.Text.Contains("规划结构"));
        Assert.Contains(entries, e => e is { Role: MessageRole.ToolCall } && e.Text.StartsWith("artifacts", StringComparison.Ordinal));
        Assert.Contains(entries, e => e is { Role: MessageRole.ToolResult, Detail: "OK" });
        Assert.Contains(entries, e => e.Role == MessageRole.Attachment && e.Text.Contains("draft.docx") && e.Text.Contains("取出的文字在下面"));
        Assert.Contains(entries, e => e.Role == MessageRole.Attachment && e.Text.Contains("photo.jpg") && e.Text.Contains("不在导出里"));
        Assert.Contains(entries, e => e is { Role: MessageRole.Note } && e.Detail is { } detail && detail.Contains("第一章 引言"));
        Assert.Equal("论文结构", GeminiSession.ReadHeader(c)!.Summary);

        var d = GeminiSession.ReadHeader(Path.Combine(_claude.ConversationsFolder, $"session-{ConvD}.json"))!;
        Assert.Null(d.Summary);
        Assert.Equal("第一句话", d.FirstUserWords);
    }

    // --------------------------------------------------------- list, reader, search, links

    [Fact]
    public void ImportedConversationsAreListedReadSearchedAndOpenedOnTheirSite()
    {
        _chatGpt.Import([ChatGptExport()]);
        _claude.Import([Zip("claude.zip", new() { ["conversations.json"] = new[] { ConversationC() } })]);

        using var catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
        var sync = new CatalogSyncService(catalog, Path.Combine(_dir, "claude"), webExports: [_chatGpt, _claude]);
        sync.Sync(new ClaudeInstallation { Found = false }, CodexPaths.FromHome(Path.Combine(_dir, "codex")));

        var rows = catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.All,
            new SortOrder(SortField.Source, false)).Rows;
        Assert.Equal([ConversationKey.ClaudeWeb, ConversationKey.ChatGptWeb, ConversationKey.ChatGptWeb],
            rows.Select(r => r.Key.Backend));
        var a = Assert.Single(rows, r => r.Key.NativeSessionId == ConvA);
        Assert.Equal("引物设计", a.Title);
        Assert.Equal(AppStanding.Imported, OrderingRules.Standing(a.Key.Backend, a.NativeArchiveState, false));
        Assert.Equal("画一只猫", Assert.Single(rows, r => r.Key.NativeSessionId == ConvB).Title);
        Assert.Equal(2, catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, new BackendFilter(AppFamily.Codex),
            new SortOrder(SortField.Source, false)).Rows.Count);

        var path = TranscriptLocator.Find(a, Path.Combine(_dir, "claude"))!;
        Assert.Equal("这是引物：ATGC", TranscriptReader.ReadTail(path, 10).Entries.Last(e => e.Role == MessageRole.Assistant).Text);

        using var index = new ContentIndex(Path.Combine(_dir, "search-index.sqlite"));
        Assert.Equal(1, index.Update([new ContentIndex.Source(ContentIndex.KeyOf(a.Key), path)]).Indexed);
        Assert.NotEmpty(index.Hits(ContentIndex.KeyOf(a.Key), "设计一对引物"));

        Assert.Equal("https://chatgpt.com/c/" + ConvA, WebLinks.Build(ConversationKey.ChatGptWeb, ConvA));
        Assert.Equal("https://claude.ai/chat/" + ConvC, WebLinks.Build(ConversationKey.ClaudeWeb, ConvC));
        Assert.Null(WebLinks.Build(ConversationKey.ChatGptWeb, "../evil"));
        Assert.Null(WebLinks.Build(ConversationKey.ClaudeWeb, ConvA + "?x=1"));
        Assert.Null(WebLinks.Build(ConversationKey.Codex, ConvA));
    }

    [Fact]
    public void AnImportedDocumentsHeaderIsReadFromItsFirstKilobytesAsFromTheWhole()
    {
        _chatGpt.Import([ChatGptExport()]);
        _claude.Import([Zip("claude.zip", new() { ["conversations.json"] = new[] { ConversationC(), ConversationD() } })]);
        var files = _chatGpt.SessionFiles().Concat(_claude.SessionFiles()).ToList();
        Assert.Equal(4, files.Count);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var whole = GeminiSession.ReadHeaderWhole(file)! with { Messages = 0 };
            Assert.Equal(whole, GeminiSession.QuickHeader(bytes, isFinalBlock: true)! with { Messages = 0 });
            Assert.Equal(whole, GeminiSession.ReadHeader(file)! with { Messages = 0 });
            // Cut short, it reads what comes first — or says it cannot, and the whole is read.
            var cut = GeminiSession.QuickHeader(bytes.AsSpan(0, bytes.Length / 2), isFinalBlock: false);
            Assert.True(cut is null || cut with { Messages = 0 } == whole);
        }

        // A Gemini CLI session — not a document of this tool's own — is always read whole.
        var cli = Encoding.UTF8.GetBytes("{\"sessionId\":\"s\",\"messages\":[{\"type\":\"user\",\"content\":[{\"text\":\"hi\"}]}],\"summary\":\"后写的标题\"}");
        Assert.Null(GeminiSession.QuickHeader(cli, isFinalBlock: true));

        // A first question longer than the first kilobytes: read whole.
        var longer = Path.Combine(_dir, "session-long.json");
        File.WriteAllBytes(longer, SessionDocument.Write(new ExportedConversation("long-1", "很长的一问", null, null,
            [new ExportedTurn(ExportedRole.User, DateTimeOffset.UnixEpoch) { Text = new string('长', 40_000) }]), "chatgpt-export", null));
        Assert.Null(GeminiSession.QuickHeader(File.ReadAllBytes(longer).AsSpan(0, 64 * 1024), isFinalBlock: false));
        Assert.Equal(GeminiSession.ReadHeaderWhole(longer), GeminiSession.ReadHeader(longer));
    }

    [Fact]
    public void AFileNameAndAPointerNameTheSameFileOfTheExport()
    {
        Assert.Equal("file-AbC123", Infrastructure.Backends.ChatGptExport.FileId("file-AbC123-gel.png"));
        Assert.Equal("file_00000000feed", Infrastructure.Backends.ChatGptExport.FileId("file_00000000feed-7b1c-4f.webp"));
        Assert.Equal("file-XyZ", Infrastructure.Backends.ChatGptExport.FileId("file-XyZ.png"));
        Assert.Null(Infrastructure.Backends.ChatGptExport.FileId("photo.png"));
        Assert.Equal("file-AbC123", Infrastructure.Backends.ChatGptExport.PointerId("file-service://file-AbC123"));
        Assert.Equal("file_00000000feed", Infrastructure.Backends.ChatGptExport.PointerId("sediment://file_00000000feed#x"));
        Assert.Null(Infrastructure.Backends.ChatGptExport.PointerId("https://example.com/a.png"));
        Assert.Equal("看这里：结果", Infrastructure.Backends.ChatGptExport.Clean("看这里citeturn0search3：结果【12†source】"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
