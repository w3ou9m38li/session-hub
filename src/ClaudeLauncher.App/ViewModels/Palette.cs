using System.Windows;
using System.Windows.Media;
using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.App.ViewModels;

/// <summary>
/// The theme's colours for view models that pick a colour per row. Read once from the theme at
/// start-up (<see cref="Load"/>) and frozen, so rows can be built on any thread; until then
/// they hold the dark theme's values.
/// </summary>
internal static class Palette
{
    public static Brush Text { get; private set; } = Frozen(0xE8, 0xE6, 0xE3);
    public static Brush Muted { get; private set; } = Frozen(0x9A, 0x97, 0x91);
    public static Brush Faint { get; private set; } = Frozen(0x7A, 0x78, 0x72);
    public static Brush Info { get; private set; } = Frozen(0x8F, 0xB8, 0xC8);
    public static Brush Success { get; private set; } = Frozen(0x86, 0xC7, 0x9A);
    public static Brush Warn { get; private set; } = Frozen(0xE0, 0xA0, 0x62);
    public static Brush Error { get; private set; } = Frozen(0xE8, 0x78, 0x7A);

    /// <summary>The two sides of a conversation in the reader.</summary>
    public static Brush UserBubble { get; private set; } = Frozen(0x33, 0x29, 0x24);
    public static Brush AssistantBubble { get; private set; } = Frozen(0x29, 0x29, 0x2E);

    /// <summary>
    /// Each app family's colour, from the theme's <c>&lt;Family&gt;TextBrush</c> (as text on the
    /// list), <c>&lt;Family&gt;FillBrush</c> (a badge under white text) and <c>&lt;Family&gt;Brush</c>
    /// (a bar of the usage chart). A family the theme has no colour for reads in the text colour.
    /// </summary>
    private static Dictionary<AppFamily, (Brush Text, Brush Fill, Brush Bar)> _families = new()
    {
        [AppFamily.Claude] = (Frozen(0xE0, 0x8B, 0x62), Frozen(0xA8, 0x55, 0x39), Frozen(0xC9, 0x64, 0x42)),
        [AppFamily.Codex] = (Frozen(0x6F, 0xBF, 0xA8), Frozen(0x35, 0x6E, 0x5F), Frozen(0x3F, 0x7F, 0x6E)),
        [AppFamily.Gemini] = (Frozen(0x95, 0xA8, 0xF0), Frozen(0x4A, 0x5C, 0xB0), Frozen(0x5B, 0x6F, 0xC9)),
        [AppFamily.DeepSeek] = (Frozen(0xC3, 0xA3, 0xF0), Frozen(0x6E, 0x4A, 0xA8), Frozen(0x8A, 0x5C, 0xC2)),
    };

    public static Brush TextOf(AppFamily family) => _families.TryGetValue(family, out var c) ? c.Text : Text;

    public static Brush FillOf(AppFamily family) => _families.TryGetValue(family, out var c) ? c.Fill : Muted;

    public static Brush BarOf(AppFamily family) => _families.TryGetValue(family, out var c) ? c.Bar : Muted;

    /// <summary>Claude's orange: also what marks the user's side in the reader and the previews.</summary>
    public static Brush ClaudeText => TextOf(AppFamily.Claude);

    /// <summary>Takes the chosen theme's colours. Called on the UI thread once the theme is applied.</summary>
    public static void Load(ResourceDictionary theme)
    {
        Text = From(theme, "TextBrush", Text);
        Muted = From(theme, "MutedBrush", Muted);
        Faint = From(theme, "FaintBrush", Faint);
        Info = From(theme, "InfoBrush", Info);
        Success = From(theme, "SuccessBrush", Success);
        Warn = From(theme, "WarnBrush", Warn);
        Error = From(theme, "ErrorBrush", Error);
        UserBubble = From(theme, "UserBubbleBrush", UserBubble);
        AssistantBubble = From(theme, "AssistantBubbleBrush", AssistantBubble);

        var families = new Dictionary<AppFamily, (Brush, Brush, Brush)>();
        foreach (var family in Sources.Families.Select(f => f.Family))
        {
            var name = family.ToString();
            families[family] = (From(theme, name + "TextBrush", TextOf(family)),
                From(theme, name + "FillBrush", FillOf(family)),
                From(theme, name + "Brush", BarOf(family)));
        }
        _families = families;      // replaced whole: rows read it from any thread
    }

    private static Brush From(ResourceDictionary theme, string key, Brush fallback)
    {
        if (theme[key] is not SolidColorBrush brush) return fallback;
        var copy = brush.Clone();
        copy.Freeze();
        return copy;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
