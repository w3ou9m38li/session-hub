using System.Text;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Settings;

/// <summary>Where and how large a window was, in device-independent units.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    /// <summary>Finite, positive, and not absurd — anything else is a damaged file, not a choice.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPlausible =>
        double.IsFinite(Left) && double.IsFinite(Top)
        && double.IsFinite(Width) && double.IsFinite(Height)
        && Width is >= 200 and <= 20000 && Height is >= 150 and <= 20000;
}

/// <summary>
/// How the user left the window: the things a person sets once and expects to find again.
/// The search in the box and the group filter are deliberately absent — reopening onto a stale
/// search that hides most of the list reads as data loss, not as a convenience. Past searches
/// are kept only as a list to pick from (<see cref="RecentSearches"/>).
/// </summary>
public sealed record LauncherSettings
{
    public WindowPlacement? MainWindow { get; init; }
    public WindowPlacement? TranscriptWindow { get; init; }

    /// <summary>List column widths the user dragged, by column key.</summary>
    public Dictionary<string, double> ColumnWidths { get; init; } = [];

    /// <summary>"All", "Claude", "Codex", "Gemini" or "DeepSeek". Anything else reads as "All".</summary>
    public string? AppFilter { get; init; }

    /// <summary>The column the list is sorted by (a <c>SortField</c> name); anything unknown reads as newest first.</summary>
    public string? SortField { get; init; }

    /// <summary>Whether that column's usual direction is flipped.</summary>
    public bool SortReversed { get; init; }

    /// <summary>The order of search results, kept apart from the list's (a <c>SortField</c> name); null reads as most relevant first.</summary>
    public string? SearchSortField { get; init; }

    public bool SearchSortReversed { get; init; }

    /// <summary>How far back the list reaches: "All", "Today", "Week", "Month" or "Quarter". Anything else reads as all.</summary>
    public string? TimeFilter { get; init; }

    /// <summary>Whether the source-coverage report was left open.</summary>
    public bool CoverageExpanded { get; init; }

    /// <summary>Transcript viewer: hide tool calls and system notes, show only the dialogue.</summary>
    public bool TranscriptDialogueOnly { get; init; }

    /// <summary>Transcript viewer: open every tool call's details.</summary>
    public bool TranscriptExpandTools { get; init; }

    /// <summary>Transcript viewer: text size, 1 = normal. Kept between 0.7 and 2.</summary>
    public double TranscriptZoom { get; init; } = 1;

    /// <summary>Ctrl+Alt+Space brings the list forward from anywhere, and puts it away again.</summary>
    public bool GlobalHotkey { get; init; } = true;

    /// <summary>The global hotkey the user chose, e.g. "Ctrl+Alt+J"; null picks the first free of the usual ones.</summary>
    public string? Hotkey { get; init; }

    /// <summary>Closing the list keeps the launcher running in the notification area.</summary>
    public bool CloseToTray { get; init; }

    /// <summary>
    /// The taskbar's jump list offers the pinned and most recent conversations. Off leaves only
    /// 「打开 Claude」「打开 ChatGPT」 there, and no conversation title with Windows.
    /// </summary>
    public bool JumpList { get; init; } = true;

    /// <summary>"Dark" or "Light"; anything else follows Windows' own app mode.</summary>
    public string? Theme { get; init; }

    /// <summary>Search what was said too, through an index of it in the data root. Off deletes the index.</summary>
    public bool ContentSearch { get; init; } = true;

    /// <summary>Usage window: how many days back it adds up (1, 7, 30, 90; 0 for all).</summary>
    public int UsageDays { get; init; } = 30;

    /// <summary>Usage window: "Claude", "Codex", "Gemini" or "DeepSeek" to count one app only; null counts them all.</summary>
    public string? UsageApp { get; init; }

    /// <summary>Usage window: "PerCall" ranks by the context a call reads on average; null by tokens in all.</summary>
    public string? UsageRank { get; init; }

    /// <summary>
    /// Folders the user added where DeepSeek Harness keeps its sessions — a <c>DSH_HOME</c>, or a
    /// deployment folder with one in its <c>data</c> folder — for a deployment that sets
    /// <c>DSH_HOME</c> only in its own start script. Kept as given.
    /// </summary>
    public List<string> DshHomes { get; init; } = [];

    /// <summary>Where the usage window was left; null opens it over the list.</summary>
    public WindowPlacement? UsageWindow { get; init; }

    /// <summary>The format a conversation was last exported in: "html", or Markdown otherwise.</summary>
    public string? ExportFormat { get; init; }

    /// <summary>Searches the user ran, newest first, offered again under the search box (<see cref="SearchHistory"/>).</summary>
    public List<string> RecentSearches { get; init; } = [];

    /// <summary>Whether searches are remembered at all. Off keeps none: the list is emptied, not hidden.</summary>
    public bool RememberSearches { get; init; } = true;
}

/// <summary>
/// Reads and writes <see cref="LauncherSettings"/> under this tool's own data root.
///
/// <para>Neither direction may cost the user anything: a missing, empty or damaged file
/// reads as the defaults, and a write that fails is reported by return value rather than
/// thrown — a preference is never worth a crash or a lost window. Writes go to a sibling
/// file first and are then moved over the original, so a crash mid-write leaves the previous
/// settings intact rather than half a JSON document.</para>
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static LauncherSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new LauncherSettings();
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return new LauncherSettings();
            var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, Options) ?? new LauncherSettings();
            return Sanitize(loaded);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new LauncherSettings();
        }
    }

    public static bool Save(string path, LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var temp = path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(temp, JsonSerializer.Serialize(Sanitize(settings), Options), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>Drops values no window could use, so a hand-edited or damaged file cannot place a window off every screen at zero size.</summary>
    private static LauncherSettings Sanitize(LauncherSettings s) => s with
    {
        MainWindow = s.MainWindow is { IsPlausible: true } main ? main : null,
        TranscriptWindow = s.TranscriptWindow is { IsPlausible: true } transcript ? transcript : null,
        UsageWindow = s.UsageWindow is { IsPlausible: true } usage ? usage : null,
        UsageDays = s.UsageDays is 1 or 7 or 30 or 90 or 0 ? s.UsageDays : 30,
        UsageApp = s.UsageApp is "Claude" or "Codex" or "Gemini" or "DeepSeek" ? s.UsageApp : null,
        UsageRank = s.UsageRank is "PerCall" ? s.UsageRank : null,
        DshHomes = (s.DshHomes ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f) && f.Length < 1024)
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList(),
        TranscriptZoom = double.IsFinite(s.TranscriptZoom) ? Math.Clamp(s.TranscriptZoom, 0.7, 2) : 1,
        // Enum.TryParse also accepts any number, so "7" would name a column that does not exist.
        SortField = Enum.TryParse<Catalog.SortField>(s.SortField, out var field) && Enum.IsDefined(field)
                    && field != Catalog.SortField.Relevance
            ? field.ToString()
            : null,
        SearchSortField = Enum.TryParse<Catalog.SortField>(s.SearchSortField, out var searchField) && Enum.IsDefined(searchField)
            ? searchField.ToString()
            : null,
        ColumnWidths = (s.ColumnWidths ?? [])
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && double.IsFinite(kv.Value) && kv.Value is >= 24 and <= 2000)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        RecentSearches = s.RememberSearches ? SearchHistory.Clean(s.RecentSearches) : [],
    };
}
