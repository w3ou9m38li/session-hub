using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Mcp;
using ClaudeLauncher.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The MCP tools read this tool's copy of a transcript its app deleted — and open the copies only
/// for the lookup, so the window can still move or remove them between calls.
/// </summary>
[Collection(nameof(LocatorArchive))]
public sealed class McpArchiveTests : IDisposable
{
    private const string Id = "6c6c3c4d-0000-4000-8000-000000000006";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-mcp-archive", Guid.NewGuid().ToString("n"));

    [Fact]
    public void ATranscriptItsAppDeletedIsReadFromTheCopyAndTheCopiesAreLetGo()
    {
        var data = Path.Combine(_dir, "data");
        var claude = Path.Combine(_dir, "claude");
        var transcript = Path.Combine(claude, "projects", "D--work", Id + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, string.Join('\n',
            JsonSerializer.Serialize(new { type = "user", message = new { content = "只在副本里还有的话" } }),
            JsonSerializer.Serialize(new { type = "assistant", message = new { content = "副本里的回答" } })) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddMinutes(-10));

        var archive = Path.Combine(data, "archive");
        using (var window = new TranscriptArchive(archive))
            Assert.Equal(1, window.Update([new TranscriptArchive.Source(transcript, "claude", Id)], TimeSpan.FromMinutes(1)).Copied);
        File.Delete(transcript);                                            // the app cleaned it up

        var catalogPath = Path.Combine(data, "catalog.sqlite");
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql"));
        var row = new SessionMetadata
        {
            Key = new ConversationKey(ConversationKey.CliCode, "local", "default", Id),
            Title = "删了原记录的会话",
            SearchKey = SearchKeys.Normalize("删了原记录的会话"),
            SourceRevision = "1:1",
            LastActivityUtc = DateTimeOffset.UtcNow,
        };
        using (var window = SqliteCatalog.OpenOrRebuild(catalogPath, schema, out _))
        {
            window.UpsertRange([row]);
            window.MarkGone(ConversationKey.CliCode, []);                 // the next sync no longer found it
        }
        using (var samePool = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = catalogPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private,
               }.ToString()))
            SqliteConnection.ClearPool(samePool);

        var options = new SessionToolsOptions(catalogPath, Path.Combine(data, "search-index.sqlite"), Path.Combine(data, "usage.sqlite"),
            Path.Combine(data, "settings.json"), claude, () => CodexPaths.FromHome(Path.Combine(_dir, "codex")))
        {
            ArchiveDirectory = archive,
        };
        var text = Call(options, "read_conversation", new { id = Id });
        Assert.Contains("只在副本里还有的话", text);
        Assert.Contains("副本里的回答", text);
        Assert.Contains("原记录已删", Call(options, "conversation_info", new { id = Id }));
        Assert.Contains(archive, Call(options, "conversation_info", new { id = Id }));      // where it was read from

        // Let go of: the lookup's copies, as the process-wide locator saw them, and every file under them.
        Assert.Null(TranscriptLocator.Archive);
        Directory.Move(archive, archive + ".moved");
        Directory.Delete(archive + ".moved", recursive: true);

        // Without copies to look in, the conversation says why it cannot be read.
        Assert.Contains("没有可读的对话记录", Call(options, "read_conversation", new { id = Id }));
    }

    private static string Call(SessionToolsOptions options, string tool, object arguments)
    {
        var server = new McpServer(new SessionTools(options), "test");
        var reply = JsonNode.Parse(server.Answer(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments },
        }), TextWriter.Null)!)!;
        return (string)reply["result"]!["content"]![0]!["text"]!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
