using System.Windows.Media;
using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.App.ViewModels;

/// <summary>Display names for backends. Tallies may carry a qualifier, e.g. "cli-code (冗余副本)".</summary>
public static class BackendNames
{
    public static string Display(string backend)
    {
        var space = backend.IndexOf(' ');
        var core = space < 0 ? backend : backend[..space];
        var qualifier = space < 0 ? string.Empty : backend[space..];
        return (Sources.Of(core)?.ReportName ?? core) + qualifier;
    }
}

public enum CoverageTone { Neutral, Ok, Warn }

/// <summary>One line of the coverage report: a headline, an optional explanation, and a tone.</summary>
public sealed class CoverageItemViewModel(string headline, string? detail, CoverageTone tone)
{
    // Frozen so they can be created on any thread and read on the UI thread.
    private static Brush NeutralBrush => Palette.Text;
    private static Brush OkBrush => Palette.Info;
    private static Brush WarnBrush => Palette.Warn;

    public string Headline { get; } = headline;
    public string? Detail { get; } = detail;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public CoverageTone Tone { get; } = tone;

    public Brush HeadlineBrush => Tone switch
    {
        CoverageTone.Ok => OkBrush,
        CoverageTone.Warn => WarnBrush,
        _ => NeutralBrush,
    };
}
