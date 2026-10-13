using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeLauncher.App;

/// <summary>
/// Hands the mouse wheel on to the list around a text box.
///
/// <para>A text box keeps a scroll viewer inside it, and that scroll viewer marks every wheel
/// event handled — even when it has nothing to scroll. In a conversation made of selectable
/// messages the list would then stop scrolling whenever the pointer is over text, which is
/// almost always. So the wheel goes to the text box only while it can still move in that
/// direction, and to its parent otherwise.</para>
/// </summary>
public static class WheelForwarding
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(WheelForwarding), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject source) return;

        if (FindScrollViewer(source) is { ScrollableHeight: > 0 } inner)
        {
            var canMove = e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight;
            if (canMove) return;
        }

        e.Handled = true;
        if (VisualTreeHelper.GetParent(source) is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender,
            });
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is TextBox box && box.Template?.FindName("PART_ContentHost", box) is ScrollViewer host) return host;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}

/// <summary>
/// Shows a text block's tooltip only when its text is cut off. A tooltip repeating text that
/// is already fully visible is noise on every row the pointer crosses.
/// </summary>
public static class TrimmedToolTip
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TrimmedToolTip), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.ToolTipOpening -= OnToolTipOpening;
        if ((bool)e.NewValue) block.ToolTipOpening += OnToolTipOpening;
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is TextBlock block && !IsTrimmed(block)) e.Handled = true;
    }

    private static bool IsTrimmed(TextBlock block)
    {
        if (string.IsNullOrEmpty(block.Text)) return false;
        var text = new FormattedText(block.Text, CultureInfo.CurrentUICulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);
        return text.WidthIncludingTrailingWhitespace > block.ActualWidth + 0.5;
    }
}

/// <summary>
/// A text block whose text marks every occurrence of a word — the search word in a snippet of
/// what was said, so the eye lands on it. Case and full/half width are ignored, as the search
/// ignores them.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TermProperty = DependencyProperty.RegisterAttached(
        "Term", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);
    public static string? GetTerm(DependencyObject d) => (string?)d.GetValue(TermProperty);
    public static void SetTerm(DependencyObject d, string? value) => d.SetValue(TermProperty, value);

    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        var text = GetText(block) ?? string.Empty;
        var term = GetTerm(block);
        block.Inlines.Clear();
        if (string.IsNullOrEmpty(term))
        {
            block.Inlines.Add(new Run(text));
            return;
        }

        var compare = CultureInfo.InvariantCulture.CompareInfo;
        var from = 0;
        while (from < text.Length)
        {
            var at = compare.IndexOf(text.AsSpan(from), term, Loose, out var length);
            if (at < 0 || length == 0) break;
            at += from;
            if (at > from) block.Inlines.Add(new Run(text[from..at]));
            block.Inlines.Add(new Run(text.Substring(at, length))
            {
                Foreground = (Brush)block.FindResource("WarnBrush"),
                FontWeight = FontWeights.SemiBold,
            });
            from = at + length;
        }
        if (from < text.Length) block.Inlines.Add(new Run(text[from..]));
    }
}

/// <summary>
/// A path that shortens itself to fit its column, keeping the folder name and dropping the
/// head: <c>…\abc\ABC进阶一套</c> rather than <c>\\wsl.localhost\ubu…</c>. The column can be
/// dragged to any width and the text follows, which a fixed character budget could not do.
/// </summary>
public sealed class CompactPathText : FrameworkElement
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(CompactPathText),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(CompactPathText),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(CompactPathText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(CompactPathText),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string? Path
    {
        get => (string?)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    private string Display => string.IsNullOrWhiteSpace(Path) ? "—" : Path;

    protected override Size MeasureOverride(Size availableSize)
    {
        var full = Format(Display);
        var width = full.WidthIncludingTrailingWhitespace;
        if (!double.IsInfinity(availableSize.Width)) width = Math.Min(width, availableSize.Width);
        return new Size(Math.Max(0, width), full.Height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = RenderSize.Width;
        if (width <= 0) return;

        var candidates = string.IsNullOrWhiteSpace(Path)
            ? ["—"]
            : Core.Display.PathDisplay.Candidates(Path);
        foreach (var candidate in candidates)
        {
            var text = Format(candidate);
            if (text.WidthIncludingTrailingWhitespace <= width + 0.5)
            {
                drawingContext.DrawText(text, new Point(0, 0));
                return;
            }
        }

        // Even the folder name alone does not fit: show as much of it as there is room for.
        var last = Format(candidates[^1]);
        last.MaxTextWidth = Math.Max(1, width);
        last.MaxLineCount = 1;
        last.Trimming = TextTrimming.CharacterEllipsis;
        drawingContext.DrawText(last, new Point(0, 0));
    }

    private FormattedText Format(string text) => new(
        text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
