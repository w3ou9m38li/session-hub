using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>A file sent with a prompt: kept in the store when the export had it (<see cref="Stored"/>), else only named.</summary>
public sealed record GeminiTakeoutFile(string Name, string? Stored, string? MimeType);

/// <summary>A page shared with a prompt — a browser tab, in Gemini in Chrome.</summary>
public sealed record GeminiTakeoutPage(string Name, string Url);

/// <summary>
/// One thing asked of Gemini on the web and its answer, as Google Takeout recorded it under
/// My Activity → Gemini Apps. <see cref="Conversation"/> is the id in the conversation's link
/// (gemini.google.com/app/&lt;id&gt;) when the record names it.
/// </summary>
public sealed record GeminiTakeoutEntry(
    string Key, string? Conversation, DateTimeOffset Time, string Prompt, string? ResponseHtml,
    List<GeminiTakeoutFile> Files, List<GeminiTakeoutPage> Pages, List<string> GeneratedImages, List<string> Notes);

/// <summary>What one read of an export found: the prompts, and how many activity records were not prompts.</summary>
/// <param name="Unreadable">Activity cards were there but none could be read (a layout or date this reader does not know).</param>
public sealed record GeminiTakeoutRead(List<GeminiTakeoutEntry> Entries, int ActivityFiles, int Skipped, bool Unreadable);

/// <summary>
/// Reads a Google Takeout export of Gemini's web history: the zip Takeout makes, or the
/// <c>MyActivity.json</c> / <c>MyActivity.html</c> inside it. Only Gemini's own activity is read,
/// and only ever read — the export is never changed.
///
/// <para>Both formats carry the same record: a title ("Prompted …"), a time, the conversation's
/// link, the answer as HTML, and what came with the prompt — files, shared pages, notes. The JSON
/// says it in fields (<c>title, time, details[].url, safeHtmlItem[].html, subtitles[], attachedFiles,
/// imageFile</c>); the HTML as one card per record: the title, a line for each thing that came with
/// it, the time in the export's own time zone, then the answer; the link under "Details:" below, a
/// picture sent to the right. Anything that is not a prompt (a cleared history, say) is skipped.</para>
/// </summary>
public static partial class GeminiTakeout
{
    /// <summary>Records with no conversation link are put together when they follow one another this closely.</summary>
    public static readonly TimeSpan ConversationGap = TimeSpan.FromMinutes(30);

    /// <summary>A file sent with a prompt is kept up to this size; larger ones are only named.</summary>
    private const long MaxKeptFile = 32L * 1024 * 1024;

    private static readonly string[] PromptPrefixes = ["Prompted ", "Prompted: ", "已提示 ", "已提示：", "提示了 "];

    [GeneratedRegex(@"^https://(?:gemini\.google\.com/app|bard\.google\.com/chat)/([0-9a-f]{8,32})(?:[/?#]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ConversationLink();

    [GeneratedRegex(@"^\d+ generated images?\.?$|^Attached \d+ files?\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex CountNote();

    [GeneratedRegex(@"^Attached (\d+) files?\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex AttachedCount();

    /// <summary>One record's raw parts, whichever format they were read from.</summary>
    private sealed record Record(
        string Title, DateTimeOffset Time, string? AnswerHtml, string? Conversation,
        List<string> FileNames, List<(string Name, string Url)> Subtitles);

    /// <summary>Whether a conversation id can go into a link of the web app: hex, as Gemini's own are.</summary>
    public static bool IsConversationId(string? id) =>
        id is { Length: >= 8 and <= 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Reads every export given. <paramref name="keepFile"/> is asked to keep each file a prompt
    /// sent that the export holds — given its name there and a way to open it — and returns the
    /// name it is kept under, or null.
    /// </summary>
    /// <param name="progress">Activity records read so far, prompts or not.</param>
    public static GeminiTakeoutRead Read(
        IReadOnlyList<string> exports, Func<string, Func<Stream>, string?>? keepFile = null,
        CancellationToken cancellation = default, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(exports);
        var entries = new List<GeminiTakeoutEntry>();
        int files = 0, skipped = 0, read = 0;
        var unreadable = false;
        foreach (var export in exports)
        {
            cancellation.ThrowIfCancellationRequested();
            if (export.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(export);
                foreach (var item in zip.Entries.ToList())
                {
                    if (!InGeminiFolder(item.FullName)) continue;
                    var folder = item.FullName[..(item.FullName.Length - item.Name.Length)];
                    string? Keep(string name)
                    {
                        if (keepFile is null) return null;
                        // Takeout keeps a JPEG it was sent as a PNG of the same name.
                        var file = zip.GetEntry(folder + name) ?? zip.GetEntry(folder + Path.ChangeExtension(name, ".png"));
                        return file is not null && file.Length <= MaxKeptFile ? keepFile(file.Name, file.Open) : null;
                    }
                    var records = Records(item.FullName, item.Open, gemini: true, ref unreadable);
                    if (records is null) continue;
                    files++;
                    foreach (var record in records)
                    {
                        Add(record, Keep, entries, ref skipped, cancellation);
                        progress?.Report(++read);
                    }
                }
            }
            else
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(export)) ?? "";
                string? Keep(string name)
                {
                    if (keepFile is null || Path.GetFileName(name) != name) return null;
                    var file = new FileInfo(Path.Combine(folder, name));
                    if (!file.Exists) file = new FileInfo(Path.Combine(folder, Path.ChangeExtension(name, ".png")));
                    return file.Exists && file.Length <= MaxKeptFile
                        ? keepFile(file.Name, () => file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        : null;
                }
                var records = Records(export,
                    () => new FileStream(export, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                    InGeminiFolder(export), ref unreadable);
                if (records is null) continue;
                files++;
                foreach (var record in records)
                {
                    Add(record, Keep, entries, ref skipped, cancellation);
                    progress?.Report(++read);
                }
            }
        }
        return new GeminiTakeoutRead(entries, files, skipped, unreadable && entries.Count == 0);
    }

    /// <summary>
    /// The conversations the records make up, oldest record first in each: by the conversation
    /// link where the record has one; records without one are put together while they follow
    /// one another within <see cref="ConversationGap"/>, and called "t" and their first record's key.
    /// </summary>
    public static List<(string Id, List<GeminiTakeoutEntry> Entries)> Conversations(IEnumerable<GeminiTakeoutEntry> entries)
    {
        var all = entries.OrderBy(e => e.Time).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
        var conversations = all.Where(e => e.Conversation is not null)
            .GroupBy(e => e.Conversation!, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Entries: g.ToList()))
            .ToList();
        List<GeminiTakeoutEntry>? run = null;
        foreach (var entry in all.Where(e => e.Conversation is null))
        {
            if (run is null || entry.Time - run[^1].Time > ConversationGap)
            {
                run = [];
                conversations.Add(("t" + entry.Key, run));
            }
            run.Add(entry);
        }
        return [.. conversations.OrderBy(c => c.Entries[0].Time)];
    }

    /// <summary>
    /// A record's identity across exports and formats: when it was asked, to the second (the HTML
    /// export has no milliseconds), in which conversation, and what — its spaces evened out.
    /// </summary>
    public static string KeyOf(DateTimeOffset time, string? conversation, string prompt)
    {
        var second = new DateTimeOffset(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var words = string.Join(' ', prompt.Replace(' ', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(
            second.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\n" + conversation + "\n" + words)), 16);
    }

    internal static string Hex(byte[] hash, int length) => Convert.ToHexStringLower(hash)[..length];

    internal static string? MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        _ => null,
    };

    /// <summary>A path inside Gemini's own folder of the export (My Activity\Gemini Apps, or Bard's before it).</summary>
    internal static bool InGeminiFolder(string path)
    {
        var parts = path.Split('/', '\\');
        return parts.Take(parts.Length - 1).Any(p =>
            p.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || p.Contains("Bard", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The records of one activity file; null when it is not one (the Gems export beside it, a picture).</summary>
    private static List<Record>? Records(string path, Func<Stream> open, bool gemini, ref bool unreadable)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = open();
            return JsonRecords(stream, gemini);
        }
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            string html;
            using (var reader = new StreamReader(open(), Encoding.UTF8)) html = reader.ReadToEnd();
            var records = HtmlRecords(html, gemini, out var cards);
            if (cards > 0 && records.Count == 0) unreadable = true;
            return cards > 0 ? records : null;
        }
        return null;
    }

    // ------------------------------------------------------------------ JSON

    private static List<Record>? JsonRecords(Stream stream, bool gemini)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var records = new List<Record>();
            var activity = false;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object || Text(r, "title") is not { } title || Time(r) is not { } time) continue;
                activity = true;
                if (!gemini && !MentionsGemini(r)) continue;
                var conversation = Links(r, "details").Concat(Links(r, "subtitles"))
                    .Select(l => ConversationLink().Match(l.Url)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
                var names = Strings(r, "attachedFiles").ToList();
                if (Text(r, "imageFile") is { } image) names.Add(image);
                var answers = r.TryGetProperty("safeHtmlItem", out var items) && items.ValueKind == JsonValueKind.Array
                    ? items.EnumerateArray().Select(i => Text(i, "html")).Where(h => !string.IsNullOrWhiteSpace(h)).ToList()
                    : [];
                records.Add(new Record(title, time, answers.Count == 0 ? null : string.Join("\n", answers), conversation,
                    names, [.. Links(r, "subtitles")]));
            }
            return activity ? records : null;
        }
    }

    private static bool MentionsGemini(JsonElement record) =>
        (Text(record, "header") ?? "").Contains("Gemini", StringComparison.OrdinalIgnoreCase)
        || Strings(record, "products").Any(p => p.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
                                                || p.Contains("Bard", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Name, string Url)> Links(JsonElement record, string property)
    {
        if (!record.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object)
                yield return (Text(item, "name") ?? "", Text(item, "url") ?? "");
    }

    private static IEnumerable<string> Strings(JsonElement record, string property)
    {
        if (!record.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && JsonText.Of(item) is { Length: > 0 } s) yield return s;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? JsonText.Of(v)
            : null;

    private static DateTimeOffset? Time(JsonElement record) =>
        Text(record, "time") is { Length: > 0 and < 64 } iso
        && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? when
            : null;

    // ------------------------------------------------------------------ HTML

    [GeneratedRegex(@"<div class=""outer-cell\b", RegexOptions.CultureInvariant)]
    private static partial Regex Card();

    [GeneratedRegex(@"<div class=""content-cell\b[^""]*""\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex CellStart();

    [GeneratedRegex(@"<p class=""mdl-typography--title"">(.*?)</p>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CardHeader();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Break();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"href=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Href();

    [GeneratedRegex(@"<img\b[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageSource();

    /// <summary>"Oct 10, 2026, 10:00:00 PM EDT" (US English), "10 Oct 2026, 22:00:00 GMT+01:00" (UK) or "2026年10月10日 22:00:00 GMT+08:00".</summary>
    [GeneratedRegex(@"^(?:(?<mon>[A-Z][a-z]{2}) (?<d>\d{1,2}), (?<y>\d{4}),|(?<d>\d{1,2}) (?<mon>[A-Z][a-z]{2,3}) (?<y>\d{4}),|(?<y>\d{4})年(?<m>\d{1,2})月(?<d>\d{1,2})日)\s*(?<h>\d{1,2}):(?<min>\d{2}):(?<s>\d{2})(?:[\s  ]?(?<ap>[AP]M))?(?:[\s  ]+(?<zone>[A-Za-z]{1,5}(?:[+-]\d{1,2}(?::?\d{2})?)?))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CardTime();

    [GeneratedRegex(@"^(GMT|UTC)([+-])(\d{1,2})(?::?(\d{2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneWithHours();

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>The time zone abbreviations Takeout writes, as offsets from UTC.</summary>
    private static readonly Dictionary<string, TimeSpan> Zones = new(StringComparer.Ordinal)
    {
        ["UTC"] = TimeSpan.Zero, ["GMT"] = TimeSpan.Zero, ["Z"] = TimeSpan.Zero,
        ["EDT"] = TimeSpan.FromHours(-4), ["EST"] = TimeSpan.FromHours(-5),
        ["CDT"] = TimeSpan.FromHours(-5), ["CST"] = TimeSpan.FromHours(-6),
        ["MDT"] = TimeSpan.FromHours(-6), ["MST"] = TimeSpan.FromHours(-7),
        ["PDT"] = TimeSpan.FromHours(-7), ["PST"] = TimeSpan.FromHours(-8),
        ["AKDT"] = TimeSpan.FromHours(-8), ["AKST"] = TimeSpan.FromHours(-9), ["HST"] = TimeSpan.FromHours(-10),
        ["BST"] = TimeSpan.FromHours(1), ["CET"] = TimeSpan.FromHours(1), ["CEST"] = TimeSpan.FromHours(2),
        ["EET"] = TimeSpan.FromHours(2), ["EEST"] = TimeSpan.FromHours(3),
        ["IST"] = new TimeSpan(5, 30, 0), ["HKT"] = TimeSpan.FromHours(8), ["SGT"] = TimeSpan.FromHours(8),
        ["JST"] = TimeSpan.FromHours(9), ["KST"] = TimeSpan.FromHours(9),
        ["AEST"] = TimeSpan.FromHours(10), ["AEDT"] = TimeSpan.FromHours(11),
    };

    /// <summary>The cards of an HTML activity page; <paramref name="cards"/> counts the cards found, read or not.</summary>
    private static List<Record> HtmlRecords(string html, bool gemini, out int cards)
    {
        var records = new List<Record>();
        var starts = Card().Matches(html).Select(m => m.Index).ToList();
        cards = 0;
        for (var i = 0; i < starts.Count; i++)
        {
            var card = html[starts[i]..(i + 1 < starts.Count ? starts[i + 1] : html.Length)];
            var cells = CellStart().Matches(card).ToList();
            if (cells.Count < 3) continue;
            cards++;
            if (!gemini && !Plain(CardHeader().Match(card).Groups[1].Value).Contains("Gemini", StringComparison.OrdinalIgnoreCase)) continue;

            string Cell(int k) => CloseCell(card[(cells[k].Index + cells[k].Length)..(k + 1 < cells.Count ? cells[k + 1].Index : card.Length)]);
            var body = Cell(0);
            var right = Cell(1);
            var caption = Cell(2);

            // The title, a line for each thing that came with the prompt, the time, the answer.
            var breaks = Break().Matches(body);
            var lines = new List<string>(breaks.Count + 1);
            var from = 0;
            foreach (Match b in breaks)
            {
                lines.Add(body[from..b.Index]);
                from = b.Index + b.Length;
            }
            lines.Add(body[from..]);
            var at = -1;
            DateTimeOffset when = default;
            for (var k = 1; k < lines.Count && at < 0; k++)
                if (CardTimeOf(Plain(lines[k])) is { } parsed) (at, when) = (k, parsed);
            if (at < 0 || at >= breaks.Count) continue;

            var subtitles = new List<(string Name, string Url)>();
            for (var k = 1; k < at; k++)
            {
                var name = Plain(lines[k]);
                var url = Href().Match(lines[k]) is { Success: true } link ? WebUtility.HtmlDecode(link.Groups[1].Value) : "";
                if (name.Length > 0 || url.Length > 0) subtitles.Add((name, url));
            }
            var files = subtitles.Where(s => s.Url.Length == 0 && s.Name.StartsWith('-'))
                .Select(s => s.Name.TrimStart('-', ' ', ' ')).Where(n => n.Length > 0).ToList();
            foreach (Match image in ImageSource().Matches(right))
                files.Add(WebUtility.HtmlDecode(image.Groups[1].Value));
            // "Attached 2 files." with fewer named: the HTML export names only some of what was sent.
            var sent = subtitles.Select(t => AttachedCount().Match(t.Name)).Where(m => m.Success)
                .Sum(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            var unnamed = sent - files.Distinct(StringComparer.Ordinal).Count();
            if (unnamed > 0) subtitles.Add(($"附带了 {unnamed} 个文件，HTML 版的导出没写是哪个（JSON 版有）", ""));

            // The answer as it stands in the card, up to the line break the card ends it with.
            var answer = body[(breaks[at].Index + breaks[at].Length)..].Trim();
            while (Break().Match(answer, Math.Max(0, answer.Length - 8)) is { Success: true } tail && tail.Index + tail.Length == answer.Length)
                answer = answer[..tail.Index].TrimEnd();

            var conversation = Href().Matches(caption).Select(m => ConversationLink().Match(WebUtility.HtmlDecode(m.Groups[1].Value)))
                .FirstOrDefault(m => m.Success)?.Groups[1].Value;
            // The title keeps the prompt's own line breaks: they are text, not <br>.
            var title = WebUtility.HtmlDecode(AnyTag().Replace(lines[0], "")).Trim();
            records.Add(new Record(title, when, answer.Length == 0 ? null : answer, conversation, files, subtitles));
        }
        return records;
    }

    /// <summary>A cell's inside: its own closing tag, and the row's and card's, taken off the end.</summary>
    private static string CloseCell(string cell)
    {
        var text = cell.TrimEnd();
        while (text.EndsWith("</div>", StringComparison.Ordinal)) text = text[..^"</div>".Length].TrimEnd();
        return text;
    }

    private static string Plain(string html) =>
        WebUtility.HtmlDecode(AnyTag().Replace(html, "")).Replace(' ', ' ').Trim();

    /// <summary>The time on a card, in UTC; null when the text is not one.</summary>
    public static DateTimeOffset? CardTimeOf(string text)
    {
        var m = CardTime().Match(text);
        if (!m.Success) return null;
        int month;
        if (m.Groups["m"].Success) month = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        else
        {
            month = Array.IndexOf(Months, m.Groups["mon"].Value[..3]) + 1;
            if (month == 0) return null;
        }
        var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        if (m.Groups["ap"].Success)
        {
            if (hour is < 1 or > 12) return null;
            hour = hour % 12 + (m.Groups["ap"].Value == "PM" ? 12 : 0);
        }
        DateTime local;
        try
        {
            local = new DateTime(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), month,
                int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture), hour,
                int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        var offset = ZoneOffset(m.Groups["zone"].Success ? m.Groups["zone"].Value : null, local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>
    /// The offset a zone written after a time stands for: an abbreviation Takeout uses, or GMT /
    /// UTC with hours. One this reader does not know is taken as this computer's own zone.
    /// </summary>
    private static TimeSpan ZoneOffset(string? zone, DateTime local)
    {
        if (zone is null) return TimeZoneInfo.Local.GetUtcOffset(local);
        if (ZoneWithHours().Match(zone) is { Success: true } m)
        {
            var span = new TimeSpan(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0, 0);
            return m.Groups[2].Value == "-" ? -span : span;
        }
        return Zones.TryGetValue(zone, out var known) ? known : TimeZoneInfo.Local.GetUtcOffset(local);
    }

    // ------------------------------------------------------------------ records → entries

    private static void Add(Record record, Func<string, string?> keep, List<GeminiTakeoutEntry> entries, ref int skipped,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var prompt = PromptOf(record.Title, out var prefixed);
        // A cleared history, a feedback note: not a prompt and nothing answered.
        if (!prefixed && record.AnswerHtml is null && record.Conversation is null)
        {
            skipped++;
            return;
        }

        var files = record.FileNames.Distinct(StringComparer.Ordinal)
            .Select(name => new GeminiTakeoutFile(name, keep(name), MimeOf(name))).ToList();

        var pages = new List<GeminiTakeoutPage>();
        var generated = new List<string>();
        var notes = new List<string>();
        foreach (var (name, url) in record.Subtitles)
        {
            if (ConversationLink().IsMatch(url)) continue;
            if (url.StartsWith("https://lh3.googleusercontent.com/", StringComparison.OrdinalIgnoreCase)) generated.Add(url);
            else if (url.Length > 0) pages.Add(new GeminiTakeoutPage(name.Length > 0 ? name : url, url));
            else if (name.Trim() is { Length: > 0 } note && !note.StartsWith('-') && !CountNote().IsMatch(note)) notes.Add(Note(note));
        }

        entries.Add(new GeminiTakeoutEntry(KeyOf(record.Time, record.Conversation, prompt), record.Conversation, record.Time,
            prompt, record.AnswerHtml, files, pages, generated, notes));
    }

    /// <summary>A note Takeout writes beside a prompt, in the reader's words where it is one this tool knows.</summary>
    private static string Note(string note) =>
        note.StartsWith("Used skill:", StringComparison.Ordinal)
            ? (note["Used skill:".Length..].Trim() is { Length: > 0 } skill ? "用了技能：" + skill : "用了技能")
            : note;

    private static string PromptOf(string title, out bool prefixed)
    {
        var normal = title.Replace(' ', ' ');
        foreach (var prefix in PromptPrefixes)
            if (normal.StartsWith(prefix, StringComparison.Ordinal))
            {
                prefixed = true;
                return normal[prefix.Length..];
            }
        prefixed = false;
        return normal;
    }
}
