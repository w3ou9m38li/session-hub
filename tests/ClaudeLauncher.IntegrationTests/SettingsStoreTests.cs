using System.Text;
using ClaudeLauncher.Infrastructure.Settings;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// A preference file is the least important thing this tool writes, so the tests are about it
/// never costing more than a preference: no crash on a bad file, no window placed nowhere.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-settings", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    [Fact]
    public void WhatIsSavedIsWhatIsLoaded()
    {
        var saved = new LauncherSettings
        {
            MainWindow = new WindowPlacement(100, 80, 1400, 900, Maximized: true),
            ColumnWidths = new() { ["group"] = 120, ["project"] = 180 },
            AppFilter = "Codex",
            CoverageExpanded = true,
            TranscriptDialogueOnly = true,
        };

        Assert.True(SettingsStore.Save(_path, saved));
        var loaded = SettingsStore.Load(_path);

        Assert.Equal(saved.MainWindow, loaded.MainWindow);
        Assert.Equal(180, loaded.ColumnWidths["project"]);
        Assert.Equal("Codex", loaded.AppFilter);
        Assert.True(loaded.CoverageExpanded);
        Assert.True(loaded.TranscriptDialogueOnly);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Theory]
    [InlineData("Claude", "Claude")]
    [InlineData("Codex", "Codex")]
    [InlineData("Gemini", "Gemini")]
    [InlineData("Copilot", null)]
    public void TheUsageWindowKeepsTheAppItCounts(string saved, string? loaded)
    {
        Assert.True(SettingsStore.Save(_path, new LauncherSettings { UsageApp = saved }));
        Assert.Equal(loaded, SettingsStore.Load(_path).UsageApp);
    }

    [Theory]
    [InlineData("PerCall", "PerCall")]
    [InlineData("Total", null)]
    [InlineData(null, null)]
    public void TheUsageWindowKeepsHowItRanks(string? saved, string? loaded)
    {
        Assert.True(SettingsStore.Save(_path, new LauncherSettings { UsageRank = saved }));
        Assert.Equal(loaded, SettingsStore.Load(_path).UsageRank);
    }

    [Fact]
    public void AMissingFileReadsAsDefaults()
    {
        var loaded = SettingsStore.Load(Path.Combine(_dir, "nope.json"));
        Assert.Null(loaded.MainWindow);
        Assert.Empty(loaded.ColumnWidths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void ADamagedFileReadsAsDefaults(string content)
    {
        File.WriteAllText(_path, content, Encoding.UTF8);
        var loaded = SettingsStore.Load(_path);
        Assert.Null(loaded.MainWindow);
    }

    [Fact]
    public void ImplausibleValuesAreDroppedRatherThanApplied()
    {
        File.WriteAllText(_path, """
            {
              "MainWindow": { "Left": 0, "Top": 0, "Width": 0, "Height": -5, "Maximized": false },
              "ColumnWidths": { "group": 5, "project": 180, "": 90 }
            }
            """, Encoding.UTF8);

        var loaded = SettingsStore.Load(_path);

        Assert.Null(loaded.MainWindow);
        Assert.Equal(["project"], loaded.ColumnWidths.Keys);
    }

    [Theory]
    [InlineData("Title", "Title")]
    [InlineData("2", "Group")]          // a number that names a column is that column
    [InlineData("7", null)]             // one that names none must not reach the window
    [InlineData("Nonsense", null)]
    public void OnlyARealSortColumnIsKept(string saved, string? expected)
    {
        File.WriteAllText(_path, $$"""{ "SortField": "{{saved}}" }""", Encoding.UTF8);

        Assert.Equal(expected, SettingsStore.Load(_path).SortField);
    }

    [Fact]
    public void RecentSearchesAreKeptCleanAndGoWhenNotWanted()
    {
        SettingsStore.Save(_path, new LauncherSettings { RecentSearches = ["kasp", "病级"] });
        var loaded = SettingsStore.Load(_path);
        Assert.Equal(["kasp", "病级"], loaded.RecentSearches);
        Assert.True(loaded.RememberSearches);

        File.WriteAllText(_path, """{ "RecentSearches": [" kasp ", "KASP", "x", null, "病级"] }""", Encoding.UTF8);
        Assert.Equal(["kasp", "病级"], SettingsStore.Load(_path).RecentSearches);

        File.WriteAllText(_path, """{ "RecentSearches": null }""", Encoding.UTF8);
        Assert.Empty(SettingsStore.Load(_path).RecentSearches);

        // Turned off: none are kept, whatever the file still says.
        File.WriteAllText(_path, """{ "RememberSearches": false, "RecentSearches": ["kasp"] }""", Encoding.UTF8);
        Assert.Empty(SettingsStore.Load(_path).RecentSearches);
    }

    [Fact]
    public void AFailedWriteReportsFalseAndLeavesTheOldFile()
    {
        SettingsStore.Save(_path, new LauncherSettings { AppFilter = "Claude" });

        // A directory where the file should go makes the move fail.
        var blocked = Path.Combine(_dir, "blocked.json");
        Directory.CreateDirectory(blocked);
        Assert.False(SettingsStore.Save(blocked, new LauncherSettings()));

        Assert.Equal("Claude", SettingsStore.Load(_path).AppFilter);
        Assert.False(File.Exists(blocked + ".tmp"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
