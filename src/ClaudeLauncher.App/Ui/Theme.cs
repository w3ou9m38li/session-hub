using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClaudeLauncher.App;

/// <summary>
/// Light or dark, chosen once at start-up: following Windows' own app mode unless the user
/// picked one. <c>Themes/Dark.xaml</c> holds every colour under a name; the light theme is the
/// same names with light values, put in place before any window looks one up — so every
/// template and window reads the chosen colours without knowing there is a choice.
/// </summary>
internal static class Theme
{
    public const string FollowWindows = "System", Dark = "Dark", Light = "Light";

    public static bool IsLight { get; private set; }

    /// <summary>Picks the theme for this run. Called before the first window exists.</summary>
    public static void Apply(ResourceDictionary theme, string? choice)
    {
        IsLight = choice switch
        {
            Light => true,
            Dark => false,
            _ => WindowsUsesLightApps(),
        };
        if (!IsLight) return;

        foreach (var (key, color) in LightColors) theme[key] = Frozen(color);
        theme[SystemColors.ControlBrushKey] = Frozen(0xFFFFFFFF);
        theme[SystemColors.HighlightBrushKey] = Frozen(LightColors["SelectionBrush"]);
        theme[SystemColors.HighlightTextBrushKey] = Frozen(LightColors["TextBrush"]);
        theme[SystemColors.InactiveSelectionHighlightBrushKey] = Frozen(LightColors["SelectionInactiveBrush"]);
        theme[SystemColors.InactiveSelectionHighlightTextBrushKey] = Frozen(LightColors["TextBrush"]);
    }

    /// <summary>Windows' 「选择你的应用模式」: light unless it says dark; light too when it cannot be read.</summary>
    public static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }

    /// <summary>What the next start would pick; differs from <see cref="IsLight"/> when Windows changed its mind.</summary>
    public static bool WouldBeLight(string? choice) => choice switch
    {
        Light => true,
        Dark => false,
        _ => WindowsUsesLightApps(),
    };

    /// <summary>The window colour, for the title bar to match.</summary>
    public static Color WindowColor => IsLight ? ToColor(LightColors["WindowBrush"]) : Color.FromRgb(0x1E, 0x1E, 0x20);

    private static SolidColorBrush Frozen(uint argb)
    {
        var brush = new SolidColorBrush(ToColor(argb));
        brush.Freeze();
        return brush;
    }

    private static Color ToColor(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>Every name Dark.xaml gives a colour, with its light value.</summary>
    private static readonly Dictionary<string, uint> LightColors = new(StringComparer.Ordinal)
    {
        ["WindowBrush"] = 0xFFF4F3F0,
        ["SurfaceBrush"] = 0xFFFFFFFF,
        ["RaisedBrush"] = 0xFFEEECE7,
        ["ControlBrush"] = 0xFFFFFFFF,
        ["HoverBrush"] = 0xFFE6E3DD,
        ["LineBrush"] = 0xFFDDD9D1,
        ["ControlLineBrush"] = 0xFFC6C1B7,
        ["TextBrush"] = 0xFF1E1D1B,
        ["MutedBrush"] = 0xFF66635D,
        ["FaintBrush"] = 0xFF8E8A83,
        ["SelectionBrush"] = 0xFFD5E4F2,
        ["SelectionInactiveBrush"] = 0xFFE4EAF0,
        ["FocusBrush"] = 0xFF2F72B5,
        ["ClaudeBrush"] = 0xFFC96442,
        ["ClaudeLineBrush"] = 0xFFB4552F,
        ["CodexBrush"] = 0xFF3F7F6E,
        ["CodexLineBrush"] = 0xFF2F6B5B,
        ["GeminiBrush"] = 0xFF5B6FC9,
        ["InfoBrush"] = 0xFF22668A,
        ["SuccessBrush"] = 0xFF2B7A47,
        ["WarnBrush"] = 0xFFA65F12,
        ["ErrorBrush"] = 0xFFC0392B,
        ["TextSelectionBrush"] = 0xFF8DB8E3,
        ["CodeBlockBrush"] = 0xFFF1EFEA,
        ["CodeSpanBrush"] = 0xFFE7E3DC,
        ["CodeTextBrush"] = 0xFF2A2926,
        ["FindHighlightBrush"] = 0xFFF6D365,
        ["FindCurrentBrush"] = 0xFFFFA84A,
        ["FindCurrentTextBrush"] = 0xFF1B1B1B,
        ["DiffAddBrush"] = 0xFFE3F4E7,
        ["DiffRemoveBrush"] = 0xFFFBE7E9,
        ["DiffAddTextBrush"] = 0xFF1A7F37,
        ["DiffRemoveTextBrush"] = 0xFFCF222E,
        ["TintHoverBrush"] = 0x14000000,
        ["TintPressedBrush"] = 0x24000000,
        ["OnAccentTextBrush"] = 0xFFFFFFFF,
        ["ControlLineHoverBrush"] = 0xFFA39E94,
        ["DetailTextBrush"] = 0xFF4A4843,
        ["CheckLineBrush"] = 0xFF9C978E,
        ["CheckLineHoverBrush"] = 0xFF6E6A63,
        ["CheckedBrush"] = 0xFF2F72B5,
        ["CheckedLineBrush"] = 0xFF285F99,
        ["SegmentHoverBrush"] = 0xFFE3E0D9,
        ["SegmentCheckedBrush"] = 0xFFFFFFFF,
        ["ItemSelectedBrush"] = 0xFFE6E3DD,
        ["PopupBrush"] = 0xFFFFFFFF,
        ["ToolTipBrush"] = 0xFFFFFFFF,
        ["ThumbBrush"] = 0xFFC8C3BA,
        ["ThumbHoverBrush"] = 0xFFAEA89E,
        ["ThumbPressedBrush"] = 0xFF958F85,
        ["DisabledTextBrush"] = 0xFFA9A59E,
        ["SubtleHoverBrush"] = 0x0F000000,
        ["HeaderTextBrush"] = 0xFF3B3935,
        ["HeaderHoverBrush"] = 0xFFE7E4DE,
        ["HeaderPressedBrush"] = 0xFFDDD9D1,
        ["RowHoverBrush"] = 0xFFF3F1ED,
        ["SelectionInactiveLineBrush"] = 0xFF94B3D1,
        ["SectionBrush"] = 0xFFF1EFEA,
        ["SubtleTextBrush"] = 0xFF33312D,
        ["MatchLineBrush"] = 0x992F72B5,
        ["DialogRowHoverBrush"] = 0xFFEEECE7,
        ["UserBubbleBrush"] = 0xFFFBEDE4,
        ["AssistantBubbleBrush"] = 0xFFF5F4F1,
        ["ClaudeTextBrush"] = 0xFFB0502C,
        ["CodexTextBrush"] = 0xFF2B7A64,
        ["ClaudeFillBrush"] = 0xFFA85539,
        ["CodexFillBrush"] = 0xFF356E5F,
        ["GeminiTextBrush"] = 0xFF3F55B5,
        ["GeminiFillBrush"] = 0xFF4A5CB0,
    };

    /// <summary>The names above, for a test that Dark.xaml defines every one of them.</summary>
    public static IEnumerable<string> LightKeys => LightColors.Keys;
}
