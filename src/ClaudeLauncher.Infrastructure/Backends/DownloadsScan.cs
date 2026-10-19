namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>An export in the Downloads folder not imported or set aside yet: whose it is, the zip, and when it arrived there.</summary>
public sealed record DownloadsOffer(ExportKinds Kind, FoundExport Export, DateTime ArrivedUtc);

/// <summary>
/// Looks in the Downloads folder, after a sync, for an export that came in since: ChatGPT's or
/// Claude's data export, a Google Takeout with Gemini's activity. Cheap when nothing came: the
/// folder's own time unchanged is the whole look — no listing, no zip opened. Otherwise one
/// listing of the zips at its top, and each zip new since the last look opened once, its list of
/// files read for every kind at once; what it is stays known while its size and time stay the
/// same. A zip that could not be opened — still downloading, locked — is asked again next time.
/// Holds no more than the zips of one listing. One look at a time.
/// </summary>
public sealed class DownloadsScan
{
    /// <summary>How long ago an export may have arrived and still be offered.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    /// <summary>How many of the newest zips one listing looks into.</summary>
    private const int Look = 40;

    private static readonly ExportKinds[] Kinds = [ExportKinds.ChatGpt, ExportKinds.Claude, ExportKinds.GeminiTakeout];

    private readonly Func<string, Stream> _open;
    private readonly object _gate = new();

    /// <summary>What each zip of the last listing is, while its size and time are the same.</summary>
    private readonly Dictionary<string, (long Bytes, DateTime WriteUtc, ExportKinds Kinds)> _known =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The exports of the last listing, the one that arrived last first.</summary>
    private List<(FoundExport Export, ExportKinds Kinds, DateTime Arrived)> _exports = [];

    private DateTime? _folderWriteUtc;
    private DateTime _listedFrom = DateTime.MaxValue;
    private long _listedAt;
    private bool _again;

    /// <param name="open">Opens a zip to read its list of files; the tests count the opens.</param>
    public DownloadsScan(string folder, Func<string, Stream>? open = null)
    {
        Folder = folder;
        _open = open ?? ExportFinder.OpenShared;
    }

    public string Folder { get; }

    /// <summary>
    /// Listed again after this long even when the folder's time says nothing came: on a drive that
    /// keeps no such time (FAT, some network folders) a download would otherwise go unseen.
    /// </summary>
    public TimeSpan RelistAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Since when an export of one app is offered: since its last import, and within the last
    /// <see cref="Window"/> — an export downloaded long ago was dealt with one way or another.
    /// </summary>
    public static DateTime NotBefore(DateTime? lastImportUtc, DateTime nowUtc) =>
        lastImportUtc is { } last && last > nowUtc - Window ? last : nowUtc - Window;

    /// <summary>
    /// The exports to offer: of each app, the one that arrived last — when it arrived after
    /// <paramref name="notBeforeUtc"/> for that app and has not been <paramref name="handled"/>.
    /// The one that arrived last comes first; a zip is offered once, as one app's. An older export
    /// is never offered in place of a newer one set aside, nor one that cannot tell ChatGPT's from Claude's.
    /// </summary>
    public IReadOnlyList<DownloadsOffer> Check(Func<ExportKinds, DateTime> notBeforeUtc, Func<FoundExport, bool> handled,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(notBeforeUtc);
        ArgumentNullException.ThrowIfNull(handled);
        lock (_gate)
        {
            var from = Kinds.ToDictionary(k => k, notBeforeUtc);
            var earliest = from.Values.Min();
            // Read before the listing: a download finishing during it is seen by the next look.
            var folder = new DirectoryInfo(Folder);
            if (!folder.Exists)
            {
                Forget();
                return [];
            }
            var written = folder.LastWriteTimeUtc;
            var same = !_again && written == _folderWriteUtc && earliest >= _listedFrom
                       && Environment.TickCount64 - _listedAt < RelistAfter.TotalMilliseconds;
            if (!same) List(written, earliest, cancellation);
            return Offers(from, handled);
        }
    }

    private void List(DateTime folderWriteUtc, DateTime earliest, CancellationToken cancellation)
    {
        List<(FileInfo File, DateTime Arrived)> zips;
        try
        {
            // The listing carries sizes, times and attributes: no more looks at each file.
            zips = new DirectoryInfo(Folder).EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly)
                .Select(f => (File: f, Arrived: ExportFinder.ArrivalOf(f)))
                .Where(z => z.Arrived > earliest && !ExportFinder.OnlineOnly(z.File.Attributes))
                .OrderByDescending(z => z.Arrived)
                .Take(Look)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Forget();
            return;
        }

        var again = false;
        var exports = new List<(FoundExport, ExportKinds, DateTime)>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, arrived) in zips)
        {
            cancellation.ThrowIfCancellationRequested();
            listed.Add(file.FullName);
            ExportKinds kinds;
            if (_known.TryGetValue(file.FullName, out var known) && known.Bytes == file.Length && known.WriteUtc == file.LastWriteTimeUtc)
                kinds = known.Kinds;
            else if (Examine(file.FullName) is { } examined)
            {
                kinds = examined;
                _known[file.FullName] = (file.Length, file.LastWriteTimeUtc, kinds);
            }
            else
            {
                again = true;       // not known what it is yet: asked again, never kept as "not an export"
                continue;
            }
            if (kinds != ExportKinds.None) exports.Add((new FoundExport(file.FullName, file.LastWriteTimeUtc, file.Length), kinds, arrived));
        }
        // Only what is still there and still within reach is remembered.
        foreach (var gone in _known.Keys.Where(k => !listed.Contains(k)).ToList()) _known.Remove(gone);

        _exports = exports;
        _folderWriteUtc = folderWriteUtc;
        _listedFrom = earliest;
        _listedAt = Environment.TickCount64;
        _again = again;
    }

    /// <summary>What a zip is; null when it could not be read just now (still downloading, locked).</summary>
    private ExportKinds? Examine(string path)
    {
        try
        {
            using var stream = _open(path);
            return ExportFinder.Classify(stream);
        }
        catch (InvalidDataException)
        {
            return ExportKinds.None;        // not a zip, or a damaged one: so it stays while its size and time do
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private List<DownloadsOffer> Offers(Dictionary<ExportKinds, DateTime> from, Func<FoundExport, bool> handled)
    {
        var offers = new List<DownloadsOffer>();
        foreach (var kind in Kinds)
        {
            var (export, _, arrived) = _exports.FirstOrDefault(e => (Offerable(e.Kinds) & kind) != 0);
            if (export is null || arrived <= from[kind]) continue;
            if (offers.Exists(o => o.Export.Path == export.Path) || handled(export)) continue;
            offers.Add(new DownloadsOffer(kind, export, arrived));
        }
        offers.Sort((a, b) => b.ArrivedUtc.CompareTo(a.ArrivedUtc));
        return offers;
    }

    /// <summary>
    /// What a zip can be offered as: a web export that cannot tell ChatGPT's from Claude's is
    /// neither — an import would not know which to read it as (dropped on the window, it is tried as ChatGPT's).
    /// </summary>
    private static ExportKinds Offerable(ExportKinds kinds)
    {
        const ExportKinds web = ExportKinds.ChatGpt | ExportKinds.Claude;
        return (kinds & web) == web ? kinds & ~web : kinds;
    }

    private void Forget()
    {
        _known.Clear();
        _exports = [];
        _folderWriteUtc = null;
        _listedFrom = DateTime.MaxValue;
        _again = false;
    }
}
