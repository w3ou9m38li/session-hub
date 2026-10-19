using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.Infrastructure.Settings;

/// <summary>
/// An export in the Downloads folder the user has dealt with — imported, set aside when the
/// status bar offered it, or found to hold nothing to import — so it is not offered again. Told
/// apart by its path, size and last write time: the same file downloaded anew is a new one.
/// </summary>
/// <param name="Kind">What it was handled as: an <see cref="ExportKinds"/> name.</param>
/// <param name="Outcome"><see cref="Imported"/>, <see cref="Dismissed"/> or <see cref="NotImportable"/>.</param>
public sealed record HandledExport(string Path, long Bytes, DateTime WriteUtc, string Kind, string Outcome)
{
    public const string Imported = "imported";
    public const string Dismissed = "dismissed";
    public const string NotImportable = "not-importable";

    /// <summary>How many are kept, newest first: far more than downloads come in between two looks.</summary>
    public const int Capacity = 32;

    public bool Is(FoundExport export) =>
        string.Equals(Path, export.Path, StringComparison.OrdinalIgnoreCase) && Bytes == export.Bytes && WriteUtc == export.SavedUtc;

    /// <summary>Whether <paramref name="export"/> was dealt with: the status bar does not offer it again.</summary>
    public static bool Contains(IReadOnlyList<HandledExport>? handled, FoundExport export) =>
        handled is not null && handled.Any(h => h.Is(export));

    /// <summary>
    /// The export an import window suggests from <paramref name="folder"/>: the newest of
    /// <paramref name="kind"/> that arrived after <paramref name="lastImport"/>. One set aside in the
    /// status bar is still suggested — the window was asked for; one found to hold nothing, or
    /// imported as the other app's, is passed over for the next older. One imported already ends
    /// the look, as in the status bar: nothing older is suggested in its place — unless the store is
    /// gone (<paramref name="lastImport"/> null, removed say), when it is the one to import again.
    /// </summary>
    public static FoundExport? Suggest(IReadOnlyList<HandledExport>? handled, ExportKinds kind, string folder, DateTime? lastImport)
    {
        var name = kind.ToString();
        HandledExport? Of(FoundExport export) => handled?.FirstOrDefault(h => h.Is(export));
        return ExportFinder.Newest(kind, folder, lastImport,
            passOver: export => Of(export) is { } h && (h.Outcome == NotImportable || h.Outcome == Imported && h.Kind != name),
            importedAlready: lastImport is null ? null : export => Of(export) is { Outcome: Imported } h && h.Kind == name);
    }

    /// <summary>The list with <paramref name="export"/> first, any earlier word on the same file gone, at most <see cref="Capacity"/> long.</summary>
    public static List<HandledExport> Add(IReadOnlyList<HandledExport>? handled, HandledExport export) =>
        Clean([export, .. handled ?? []]);

    /// <summary>
    /// Only entries the offer can use — a full path, a size, a known kind and outcome — one per
    /// file, newest first, at most <see cref="Capacity"/>: a hand-edited or damaged file is not a choice.
    /// </summary>
    public static List<HandledExport> Clean(IReadOnlyList<HandledExport>? handled)
    {
        var clean = new List<HandledExport>();
        foreach (var h in handled ?? [])
        {
            if (h is null || string.IsNullOrWhiteSpace(h.Path) || h.Path.Length > 1024 || !System.IO.Path.IsPathFullyQualified(h.Path)
                || h.Bytes < 0 || h.Outcome is not (Imported or Dismissed or NotImportable)
                || !Enum.TryParse<ExportKinds>(h.Kind, out var kind) || kind is not (ExportKinds.ChatGpt or ExportKinds.Claude or ExportKinds.GeminiTakeout)
                || !h.Kind.All(char.IsAsciiLetter))
                continue;
            var entry = h.WriteUtc.Kind == DateTimeKind.Utc ? h
                : h with { WriteUtc = h.WriteUtc.Kind == DateTimeKind.Local ? h.WriteUtc.ToUniversalTime() : DateTime.SpecifyKind(h.WriteUtc, DateTimeKind.Utc) };
            if (clean.Exists(c => string.Equals(c.Path, entry.Path, StringComparison.OrdinalIgnoreCase) && c.Bytes == entry.Bytes
                                  && c.WriteUtc == entry.WriteUtc))
                continue;
            clean.Add(entry);
            if (clean.Count == Capacity) break;
        }
        return clean;
    }
}
