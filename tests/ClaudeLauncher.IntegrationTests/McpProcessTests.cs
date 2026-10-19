using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Mcp;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The program itself started as an agent starts it — <c>--mcp</c>, pipes for stdin and stdout —
/// on a data root of the test's own: it answers, writes nothing but protocol to stdout, and
/// ends when its input does. Its data root and Claude's and Codex's folders are the test's own.
/// </summary>
public sealed class McpProcessTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-mcp-process", Guid.NewGuid().ToString("n"));

    /// <summary>The program as the solution build left it, in the same configuration as these tests; null when it was not built.</summary>
    private static string? Program()
    {
        var here = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);   // tests\…\bin\<config>\net10.0-windows
        var configuration = new DirectoryInfo(here).Parent?.Name ?? "Debug";
        for (var folder = new DirectoryInfo(here); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "ClaudeLauncher.slnx")))
            {
                var exe = Path.Combine(folder.FullName, "src", "ClaudeLauncher.App", "bin", configuration, "net10.0-windows",
                    "ClaudeLauncher.App.exe");
                return File.Exists(exe) ? exe : null;
            }
        return null;
    }

    [Fact]
    public async Task TheProgramServesAnAgentOverPipesAndEndsWithItsInput()
    {
        if (Program() is not { } exe)
        {
            output.WriteLine("SKIPPED: ClaudeLauncher.App.exe is not built — build the solution first (dotnet build ClaudeLauncher.slnx).");
            return;
        }

        var root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(root);
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql"));
        var catalog = Path.Combine(root, "catalog.sqlite");
        using (var window = SqliteCatalog.OpenOrRebuild(catalog, schema, out _))
            window.UpsertRange([new SessionMetadata
            {
                Key = new ConversationKey(ConversationKey.CliCode, "local", "default", "4f4f3c4d-0000-4000-8000-000000000004"),
                Title = "进程里看到的会话",
                SearchKey = SearchKeys.Normalize("进程里看到的会话"),
                SourceRevision = "1:1",
                LastActivityUtc = DateTimeOffset.UtcNow.AddHours(-1),
                NativeArchiveState = NativeArchiveState.Unsupported,
            }]);
        // The window closed: its pooled connection really let go of the file (only this catalog's pool).
        using (var samePool = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = catalog, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private,
               }.ToString()))
            SqliteConnection.ClearPool(samePool);

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add(McpHost.Argument);
        // This machine's data root, Claude's and Codex's folders are all somewhere else for this run.
        start.Environment["CLAUDE_LAUNCHER_DATA_DIR"] = root;
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(_dir, "claude");
        start.Environment["CODEX_HOME"] = Path.Combine(_dir, "codex");

        using var process = Process.Start(start)!;
        try
        {
            var errors = process.StandardError.ReadToEndAsync();
            var lines = process.StandardOutput.ReadToEndAsync();
            foreach (var request in new object[]
                     {
                         new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } } },
                         new { jsonrpc = "2.0", method = "notifications/initialized" },
                         new { jsonrpc = "2.0", id = 2, method = "tools/list" },
                         new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "list_recent", arguments = new { } } },
                         new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "search_conversations", arguments = new { query = "看到" } } },
                     })
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();                                  // the agent goes away

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(stop.Token);
            var stdout = await lines;
            output.WriteLine(await errors);

            Assert.Equal(0, process.ExitCode);
            // Nothing but protocol on stdout: every line one JSON-RPC answer, one per request.
            var answers = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
            Assert.Equal([1, 2, 3, 4], answers.Select(a => (int)a["id"]!));
            Assert.All(answers, a => Assert.Equal("2.0", (string?)a["jsonrpc"]));
            Assert.DoesNotContain('\r', stdout);
            Assert.Equal("session-hub", (string?)answers[0]["result"]!["serverInfo"]!["name"]);
            Assert.Equal(6, answers[1]["result"]!["tools"]!.AsArray().Count);
            Assert.Contains("进程里看到的会话", (string)answers[2]["result"]!["content"]![0]!["text"]!);
            Assert.Contains("进程里看到的会话", (string)answers[3]["result"]!["content"]![0]!["text"]!);
            Assert.Null(answers[3]["result"]!["isError"]);
            Assert.Contains(root, await errors);                            // the log says which data root, on stderr
            Assert.False(File.Exists(Path.Combine(root, "diagnostics", McpHost.CrashFileName)));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task WithNothingToReadItEndsAtOnce()
    {
        if (Program() is not { } exe)
        {
            output.WriteLine("SKIPPED: ClaudeLauncher.App.exe is not built — build the solution first (dotnet build ClaudeLauncher.slnx).");
            return;
        }
        var root = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(root);
        // Input that ends before anything is said — as it does at once for a double-click, which has no stdin.
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        start.ArgumentList.Add(McpHost.Argument);
        start.Environment["CLAUDE_LAUNCHER_DATA_DIR"] = root;
        using var process = Process.Start(start)!;
        try
        {
            process.StandardInput.Close();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(stop.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.False(File.Exists(Path.Combine(root, "catalog.sqlite")));        // nothing made
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
