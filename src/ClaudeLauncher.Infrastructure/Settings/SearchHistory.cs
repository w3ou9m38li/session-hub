using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.Infrastructure.Settings;

/// <summary>
/// The searches the user ran, newest first, offered again under the search box. Only the rules
/// of the list live here; <see cref="LauncherSettings.RecentSearches"/> holds it, and the window
/// decides when a search counts as run.
/// </summary>
public static class SearchHistory
{
    /// <summary>How many are kept: all of them fit in the list under the box without scrolling.</summary>
    public const int Capacity = 12;

    /// <summary>Longer than this is a pasted paragraph, not a search worth offering again.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// The form a search is kept in — trimmed, each run of white space one space — or null when
    /// it is not worth keeping: a single character, or longer than <see cref="MaxLength"/>.
    /// </summary>
    public static string? Normalize(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var kept = string.Join(' ', search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return kept.Length is >= 2 and <= MaxLength ? kept : null;
    }

    /// <summary>Whether the list already holds a search that finds the same as <paramref name="search"/>.</summary>
    public static bool Contains(IReadOnlyList<string> history, string search)
    {
        var key = SearchKeys.Normalize(search);
        return history.Any(entry => SearchKeys.Normalize(entry) == key);
    }

    /// <summary>
    /// The list with <paramref name="search"/> first. An entry that finds the same — upper and
    /// lower case, full and half width aside — moves up in the newest spelling instead of being
    /// repeated. <paramref name="replacing"/> is what the same search was kept as a moment
    /// earlier, while it was still being typed: it goes when the new one grows from it or cuts
    /// it back, so typing 「kasp primer」 slowly leaves one entry, not every stage of it.
    /// </summary>
    public static List<string> Add(IReadOnlyList<string> history, string search, string? replacing = null)
    {
        if (Normalize(search) is not { } kept) return [.. history];
        var key = SearchKeys.Normalize(kept);
        var earlier = replacing is null ? null : SearchKeys.Normalize(replacing);
        var refines = !string.IsNullOrEmpty(earlier)
                      && (key.StartsWith(earlier, StringComparison.Ordinal) || earlier.StartsWith(key, StringComparison.Ordinal));

        var list = new List<string>(Capacity) { kept };
        foreach (var entry in history)
        {
            if (list.Count == Capacity) break;
            var other = SearchKeys.Normalize(entry);
            if (other == key || (refines && other == earlier)) continue;
            list.Add(entry);
        }
        return list;
    }

    /// <summary>The list without <paramref name="search"/>, nor anything that finds the same.</summary>
    public static List<string> Remove(IReadOnlyList<string> history, string search)
    {
        var key = SearchKeys.Normalize(search);
        return history.Where(entry => SearchKeys.Normalize(entry) != key).ToList();
    }

    /// <summary>
    /// One search box's part in the list: the entry its search was kept as while still being
    /// typed, which the next stage of the same search replaces. A search the list already held
    /// only moves up, and stays when it is typed on into a new one. Starts over when the box is
    /// emptied or a search is picked from the list.
    /// </summary>
    public sealed class Typing
    {
        private string? _kept;

        public void StartOver() => _kept = null;

        /// <summary>The list with the box's search kept in it; unchanged when it is not worth keeping.</summary>
        public List<string> Keep(IReadOnlyList<string> history, string? search)
        {
            if (Normalize(search) is not { } kept) return [.. history];
            // The same stage again (the box lost focus after it stood a moment): only moved up.
            if (_kept is not null && SearchKeys.Normalize(_kept) == SearchKeys.Normalize(kept)) return Add(history, kept);
            var known = Contains(history, kept);
            var updated = Add(history, kept, _kept);
            _kept = known ? null : kept;
            return updated;
        }
    }

    /// <summary>
    /// A stored list made safe to show: every entry in its kept form, the unfit and the repeats
    /// dropped, at most <see cref="Capacity"/> — a hand-edited or damaged file costs nothing.
    /// </summary>
    public static List<string> Clean(IEnumerable<string?>? stored)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in stored ?? [])
        {
            if (Normalize(entry) is not { } kept || !seen.Add(SearchKeys.Normalize(kept))) continue;
            list.Add(kept);
            if (list.Count == Capacity) break;
        }
        return list;
    }
}
