using System.Text;
using System.Text.Json;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Changes;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>The files a conversation changed, read back from its transcript.</summary>
public sealed class ChangeScannerTests : IDisposable
{
    private readonly string _dir;

    public ChangeScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-changes", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private string Write(params object[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines.Select(l => l as string ?? JsonSerializer.Serialize(l))) + '\n',
            new UTF8Encoding(false));
        return path;
    }

    private static object Call(string id, string name, object input, string at = "2026-09-01T08:00:00Z") => new
    {
        type = "assistant",
        timestamp = at,
        message = new { role = "assistant", content = new object[] { new { type = "tool_use", id, name, input } } },
    };

    private static object Result(string id, object? recorded, bool error = false) => new
    {
        type = "user",
        timestamp = "2026-09-01T08:00:05Z",
        message = new { role = "user", content = new object[] { new { tool_use_id = id, type = "tool_result", is_error = error, content = "done" } } },
        toolUseResult = recorded,
    };

    [Fact]
    public void ClaudeEditsAreReadFromThePatchTheyRecorded()
    {
        var path = Write(
            Call("t1", "Edit", new { file_path = @"D:\p\a.py", old_string = "x = 1", new_string = "x = 2" }),
            Result("t1", new
            {
                filePath = @"D:\p\a.py",
                oldString = "x = 1",
                newString = "x = 2",
                originalFile = new string('#', 5000),
                structuredPatch = new[]
                {
                    new { oldStart = 10, oldLines = 3, newStart = 10, newLines = 3, lines = new[] { " a", "-x = 1", "+x = 2", " b" } },
                },
            }));

        var change = Assert.Single(ChangeScanner.Read(path, codex: false));
        Assert.Equal(@"D:\p\a.py", change.Path);
        Assert.Equal(ChangeKind.Updated, change.Kind);
        Assert.True(change.Succeeded);
        Assert.Equal((1, 1), (change.Added, change.Removed));
        Assert.Equal('@', change.Lines[0].Kind);
        var removed = change.Lines.Single(l => l.Kind == '-');
        Assert.Equal(("x = 1", 11), (removed.Text, removed.Old));
        Assert.Equal(11, change.Lines.Single(l => l.Kind == '+').New);
    }

    [Fact]
    public void AFailedEditIsShownAsTriedAndAWriteWithoutAResultAsAsked()
    {
        var path = Write(
            Call("t1", "Edit", new { file_path = @"D:\p\a.py", old_string = "keep\nold line\nkeep2", new_string = "keep\nnew line\nkeep2" }),
            Result("t1", "Error: String to replace not found in file.", error: true),
            Call("t2", "Write", new { file_path = @"D:\p\b.md", content = "one\ntwo\n" }, "2026-09-01T08:01:00Z"),
            Call("t3", "Bash", new { command = "ls" }));

        var changes = ChangeScanner.Read(path, codex: false);
        Assert.Equal(2, changes.Count);

        var tried = changes[0];
        Assert.False(tried.Succeeded);
        Assert.Equal(["keep", "old line", "new line", "keep2"], tried.Lines.Select(l => l.Text).ToArray());
        Assert.Equal(" -+ ", new string([.. tried.Lines.Select(l => l.Kind)]));

        var written = changes[1];
        Assert.Equal(@"D:\p\b.md", written.Path);
        Assert.Equal(["one", "two"], written.Lines.Select(l => l.Text).ToArray());
        Assert.Equal(2, written.Added);
    }

    [Fact]
    public void CodexUsesTheDiffItAppliedAndOtherwiseThePatch()
    {
        var applied = "*** Begin Patch\n*** Update File: src/a.py\n@@\n-old\n+new\n*** End Patch";
        var raw = "*** Begin Patch\n*** Add File: docs/n.md\n+hello\n*** Update File: src/b.py\n*** Move to: src/c.py\n@@ def f():\n-    return 1\n+    return 2\n*** Delete File: tmp.txt\n*** End Patch";
        var path = Write(
            new { timestamp = "2026-09-01T08:00:00Z", type = "turn_context", payload = new { cwd = @"G:\proj", model = "gpt-5.5" } },
            new { timestamp = "2026-09-01T08:00:01Z", type = "response_item", payload = new { type = "custom_tool_call", call_id = "c1", name = "apply_patch", input = applied } },
            new
            {
                timestamp = "2026-09-01T08:00:02Z",
                type = "event_msg",
                payload = new
                {
                    type = "patch_apply_end",
                    call_id = "c1",
                    success = true,
                    changes = new Dictionary<string, object>
                    {
                        [@"G:\proj\src\a.py"] = new { type = "update", unified_diff = "@@ -5,2 +5,2 @@\n ctx\n-old\n+new\n" },
                    },
                },
            },
            new { timestamp = "2026-09-01T08:01:00Z", type = "response_item", payload = new { type = "custom_tool_call", call_id = "c2", name = "apply_patch", input = raw } });

        var changes = ChangeScanner.Read(path, codex: true);
        Assert.Equal(4, changes.Count);

        var a = changes[0];
        Assert.Equal(@"G:\proj\src\a.py", a.Path);
        Assert.Equal(6, a.Lines.Single(l => l.Kind == '-').Old);        // numbered from the applied diff

        Assert.Equal((@"G:\proj\docs\n.md", ChangeKind.Added), (changes[1].Path, changes[1].Kind));
        Assert.Equal(@"G:\proj\src\c.py", changes[2].MovedTo);
        Assert.Equal((1, 1), (changes[2].Added, changes[2].Removed));
        Assert.Equal((@"G:\proj\tmp.txt", ChangeKind.Deleted), (changes[3].Path, changes[3].Kind));
    }

    [Fact]
    public void LinesThatLookLikeFileHeadersInsideAHunkAreContent()
    {
        var path = Write(
            new { timestamp = "2026-09-01T08:00:01Z", type = "response_item", payload = new { type = "custom_tool_call", call_id = "c1", name = "apply_patch", input = "*** Begin Patch\n*** Update File: G:\\p\\a.sql\n@@\n--- x\n*** End Patch" } },
            new
            {
                timestamp = "2026-09-01T08:00:02Z",
                type = "event_msg",
                payload = new
                {
                    type = "patch_apply_end",
                    call_id = "c1",
                    success = true,
                    changes = new Dictionary<string, object>
                    {
                        [@"G:\p\a.sql"] = new { type = "update", unified_diff = "@@ -1,3 +1,3 @@\n a\n--- old comment\n+++i;\n b\n" },
                    },
                },
            });

        var change = Assert.Single(ChangeScanner.Read(path, codex: true));
        Assert.Equal((1, 1), (change.Added, change.Removed));
        var removed = change.Lines.Single(l => l.Kind == '-');
        Assert.Equal(("-- old comment", 2), (removed.Text, removed.Old));
        var added = change.Lines.Single(l => l.Kind == '+');
        Assert.Equal(("++i;", 2), (added.Text, added.New));
        Assert.Equal(3, change.Lines.Last().Old);
    }

    [Fact]
    public void ALargeWriteThatBeginsAtTheEndOfABlockIsStillRead()
    {
        var filler = new string('x', 1024 * 1024 - 60);
        var path = Write(
            new { type = "user", message = new { content = filler } },
            Call("t9", "Write", new { file_path = @"D:\p\big.txt", content = new string('z', 1100 * 1024) + "\nend" }));

        var change = Assert.Single(ChangeScanner.Read(path, codex: false));
        Assert.Equal(@"D:\p\big.txt", change.Path);
        Assert.Equal(2, change.Added);
    }

    [Fact]
    public void SubagentsAndSpawnedTasksAreRelatedButForksAreNot()
    {
        var project = Path.Combine(_dir, "projects", "p");
        var transcript = Path.Combine(project, "abc.jsonl");
        Directory.CreateDirectory(Path.Combine(project, "abc", "subagents"));
        File.WriteAllText(transcript, "\n");
        File.WriteAllText(Path.Combine(project, "abc", "subagents", "agent-1.jsonl"), "\n");
        Assert.Equal(["子代理"], ChangeScanner.Related(transcript, codex: false, null).Select(r => r.Label).ToArray());

        var sessions = Path.Combine(_dir, "codex", "sessions", "2026", "09", "14");
        Directory.CreateDirectory(sessions);
        var codex = new CodexPaths(Path.Combine(_dir, "codex"), Path.Combine(_dir, "codex", "state.sqlite"),
            Path.Combine(_dir, "codex", "sessions"), Path.Combine(_dir, "codex", "archived_sessions"));
        string Rollout(string time, string id, object payload)
        {
            var file = Path.Combine(sessions, $"rollout-2026-09-14T{time}-{id}.jsonl");
            File.WriteAllText(file, JsonSerializer.Serialize(new { type = "session_meta", payload }) + "\n");
            return file;
        }
        var root = "01a07b31-0271-72b3-acc1-e69a725e36d3";
        var main = Rollout("10-00-00", root, new { id = root, session_id = root });
        var child = Rollout("10-05-00", "01a09df0-c756-7970-91cf-aec1d36fb0a5",
            new { id = "01a09df0-c756-7970-91cf-aec1d36fb0a5", session_id = root, parent_thread_id = root });
        var grandchild = Rollout("10-06-00", "01a09df1-c756-7970-91cf-aec1d36fb0a5",
            new { id = "01a09df1-c756-7970-91cf-aec1d36fb0a5", session_id = root, parent_thread_id = "01a09df0-c756-7970-91cf-aec1d36fb0a5" });
        Rollout("10-07-00", "01a09df2-c756-7970-91cf-aec1d36fb0a5",
            new { id = "01a09df2-c756-7970-91cf-aec1d36fb0a5", session_id = "01a09df2-c756-7970-91cf-aec1d36fb0a5", forked_from_id = root });
        Rollout("09-00-00", "01a09df3-c756-7970-91cf-aec1d36fb0a5",          // started before it: not its own
            new { id = "01a09df3-c756-7970-91cf-aec1d36fb0a5", parent_thread_id = root });

        var related = ChangeScanner.Related(main, codex: true, codex).Select(r => r.Path).Order().ToArray();
        Assert.Equal(new[] { child, grandchild }.Order().ToArray(), related);
    }

    [Fact]
    public void APatchInsideAScriptsStringIsFound()
    {
        var script = "const patch = \"*** Begin Patch\\n*** Update File: G:\\\\x\\\\y.ts\\n@@\\n-a\\n+b \\u4e2d\\n*** End Patch\";\nawait tools.apply_patch(patch);";
        var path = Write(
            new { timestamp = "2026-09-01T08:00:01Z", type = "response_item", payload = new { type = "custom_tool_call", call_id = "c9", name = "exec", input = script } });

        var change = Assert.Single(ChangeScanner.Read(path, codex: true));
        Assert.Equal(@"G:\x\y.ts", change.Path);
        Assert.Equal("exec", change.Tool);
        Assert.Equal("b 中", change.Lines.Single(l => l.Kind == '+').Text);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
