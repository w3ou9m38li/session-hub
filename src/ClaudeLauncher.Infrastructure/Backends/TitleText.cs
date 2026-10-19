using System.Text.RegularExpressions;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// A title made from what someone typed, tidied for a list: the file an <c>@"C:\…\稿(3).docx"</c>
/// mention names, the words of a <c>[文字](链接)</c> link, one line. Only titles inferred from
/// message text go through here — a name the user gave, or one the app generated, is kept as is.
/// </summary>
public static partial class TitleText
{
    public const int MaxLength = 120;

    /// <summary>
    /// <c>[label](target)</c>: the label is what is read, the target is where it points. A
    /// target may hold spaces (a folder called "Meteorological Data"), and one an app cut
    /// short may never close.
    /// </summary>
    [GeneratedRegex(@"\[([^\[\]\n]{1,200})\]\([^()\n]{1,1000}(?:\)|$)")]
    private static partial Regex MarkdownLink();

    /// <summary>
    /// A file mentioned to Claude Code: <c>@"path with spaces"</c> or <c>@path\with\slashes</c>.
    /// Not an e-mail address: the <c>@</c> starts a word, and a bare path has a slash in it.
    /// </summary>
    [GeneratedRegex(@"(?<![\w.@])@(?:""([^""\n]{1,1000})""|([^\s""]*[\\/][^\s""]*))")]
    private static partial Regex FileMention();

    public static string Tidy(string text)
    {
        var tidy = MarkdownLink().Replace(text, m => m.Groups[1].Value);
        tidy = FileMention().Replace(tidy, m => FileName(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
        tidy = string.Join(' ', tidy.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return tidy.Length > MaxLength ? tidy[..MaxLength] : tidy;
    }

    /// <summary>The last part of a path; a folder's own name when the path ends in a slash.</summary>
    private static string FileName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var name = cut < 0 ? trimmed : trimmed[(cut + 1)..];
        return name.Length > 0 ? name : path;
    }
}
