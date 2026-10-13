using System.Windows;
using System.Windows.Media;

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

    /// <summary>Claude's orange and ChatGPT's green, as text on the list.</summary>
    public static Brush ClaudeText { get; private set; } = Frozen(0xE0, 0x8B, 0x62);
    public static Brush CodexText { get; private set; } = Frozen(0x6F, 0xBF, 0xA8);
    public static Brush GeminiText { get; private set; } = Frozen(0x95, 0xA8, 0xF0);

    /// <summary>The same two as badge backgrounds under white text.</summary>
    public static Brush ClaudeFill { get; private set; } = Frozen(0xA8, 0x55, 0x39);
    public static Brush CodexFill { get; private set; } = Frozen(0x35, 0x6E, 0x5F);
    public static Brush GeminiFill { get; private set; } = Frozen(0x4A, 0x5C, 0xB0);

    /// <summary>The two sides of a conversation in the reader.</summary>
    public static Brush UserBubble { get; private set; } = Frozen(0x33, 0x29, 0x24);
    public static Brush AssistantBubble { get; private set; } = Frozen(0x29, 0x29, 0x2E);

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
        ClaudeText = From(theme, "ClaudeTextBrush", ClaudeText);
        CodexText = From(theme, "CodexTextBrush", CodexText);
        ClaudeFill = From(theme, "ClaudeFillBrush", ClaudeFill);
        CodexFill = From(theme, "CodexFillBrush", CodexFill);
        GeminiText = From(theme, "GeminiTextBrush", GeminiText);
        GeminiFill = From(theme, "GeminiFillBrush", GeminiFill);
        UserBubble = From(theme, "UserBubbleBrush", UserBubble);
        AssistantBubble = From(theme, "AssistantBubbleBrush", AssistantBubble);
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
