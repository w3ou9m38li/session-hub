using ClaudeLauncher.Infrastructure.Semantic;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The program itself as the worker, in a process of its own, as the window starts it: a copy of
/// the built program in a folder of its own — the way it is handed out — first without the
/// optional component beside it, then with it, on a data root in a temp folder and a tiny model
/// written here. Nothing else on this machine is touched. SESSION_HUB_TEST_PROGRAM can name a
/// published exe (the single-file one from scripts\Publish-Share.ps1) to try that instead.
/// </summary>
public sealed class SemanticProcessTests : IDisposable
{
    /// <summary>
    /// The worker runs below normal priority, as the window starts it: on a machine busy with
    /// other work it waits its turn, and a first start of a fresh copy is scanned by antivirus.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    private readonly SemanticRoot _root = new();
    private readonly string _exeFolder;
    private readonly string _exeName = "ClaudeLauncher.App.exe";
    private readonly string _modelFolder;

    public SemanticProcessTests()
    {
        _exeFolder = Path.Combine(_root.Folder, "program");
        if (Environment.GetEnvironmentVariable("SESSION_HUB_TEST_PROGRAM") is { Length: > 0 } published)
        {
            _exeName = Path.GetFileName(published);
            Directory.CreateDirectory(_exeFolder);
            File.Copy(published, Exe);
        }
        else
        {
            Copy(EmbedderLoaderTests.BuildOutput("ClaudeLauncher.App"), _exeFolder);
        }
        Assert.True(File.Exists(Exe), $"程序不在 {Exe}：先构建整个解决方案（dotnet build ClaudeLauncher.slnx）");

        _modelFolder = Directory.CreateDirectory(Path.Combine(_root.Folder, "models", "tiny")).FullName;
        File.WriteAllBytes(Path.Combine(_modelFolder, "model.onnx"), EmbedderLoaderTests.TinyModel.Tokens());
        File.WriteAllLines(Path.Combine(_modelFolder, "vocab.txt"), EmbedderLoaderTests.Vocabulary);
        // Every [CLS] vector is alike in a model this small: the mean of the words tells texts apart.
        File.WriteAllText(Path.Combine(_modelFolder, "embedding.json"), """{ "pooling": "mean" }""");

        _root.Write("a", "引物设计引物设计", "设计引物设计引物");
        _root.Write("b", "红烧肉红烧肉红烧肉", "红烧肉烧红烧肉红");
        _root.List("a", "b");
        _root.IndexContent();
    }

    private string Exe => Path.Combine(_exeFolder, _exeName);

    /// <summary>A build's folder without the other platforms' native files: what a win-x64 copy holds.</summary>
    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts[0] == EmbedderLoader.ComponentFolderName) continue;
            if (parts[0] == "runtimes" && parts.Length > 1 && parts[1] != "win-x64") continue;
            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Fact]
    public async Task TheProgramBuildsAndSearchesWithTheComponentBesideItAndSaysWhatIsMissingWithout()
    {
        // No component beside the program: the worker says so, and ends.
        using (var bare = SemanticClient.ForWorker(Exe, _root.Folder, _modelFolder))
        {
            var missing = await Assert.ThrowsAsync<SemanticUnavailableException>(() => bare.StatusAsync().WaitAsync(Patience));
            Assert.Contains("没有找到语义搜索组件", missing.Message);
            Assert.Contains(Path.Combine(_exeFolder, "semantic", "ClaudeLauncher.Semantic.dll"), missing.Message);
            Assert.False(bare.IsRunning);
        }

        Copy(EmbedderLoaderTests.ComponentFolder(), Path.Combine(_exeFolder, EmbedderLoader.ComponentFolderName));
        using var client = SemanticClient.ForWorker(Exe, _root.Folder, _modelFolder);
        var built = new TaskCompletionSource<SemanticBuilt>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Built += b => built.TrySetResult(b);

        var before = await client.StatusAsync().WaitAsync(Patience);
        Assert.Equal(0, before.Chunks);
        Assert.StartsWith("tiny:", before.ModelId);
        Assert.Equal(SemanticHost.ModelIdOf(_modelFolder), before.ModelId);      // the window tells the model's id the same way

        Assert.Equal(BuildStart.Started, await client.BuildAsync().WaitAsync(Patience));
        var run = await built.Task.WaitAsync(Patience);
        Assert.Equal(2, run.Conversations);
        Assert.Equal(4, run.Chunks);
        Assert.True(File.Exists(_root.IndexPath));                                // in the data root it was handed, nowhere else

        var hits = await client.QueryAsync("红烧肉").WaitAsync(Patience);
        Assert.Equal(SemanticRoot.KeyOf("b"), hits[0].Conversation);
        Assert.Equal("红烧肉红烧肉红烧肉", hits[0].Chunks[0].Snippet);
        hits = await client.QueryAsync("引物设计").WaitAsync(Patience);
        Assert.Equal(SemanticRoot.KeyOf("a"), hits[0].Conversation);

        await client.CloseAsync(Patience);
        Assert.False(client.IsRunning);
    }

    public void Dispose() => _root.Dispose();
}
