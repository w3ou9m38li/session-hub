using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeLauncher.App.ViewModels;
using ClaudeLauncher.Infrastructure.Usage;

namespace ClaudeLauncher.App;

/// <summary>
/// One bar per call, as tall as the context it read: the part that came from the cache and the
/// part read in fresh; a call that found the cache expired in a colour of its own, a compaction
/// marked above its call, a long pause by a tick below. The bars are a few geometries drawn
/// once per size — not an element per call — so a conversation of thousands of calls costs
/// about what one of ten does. Pointing at a bar tells its numbers.
/// </summary>
public sealed class ContextChart : FrameworkElement
{
    private const double Top = 9;           // room for a compaction's mark
    private const double Bottom = 6;        // room for a pause's tick

    private IReadOnlyList<AnalysisCall> _calls = [];
    private long _scale = 1;
    private DrawingGroup? _drawing;
    private Size _drawnFor;
    private int _hover = -1;
    private ToolTip? _tip;

    /// <summary>Shows these calls, or nothing; scaled to the largest context among them.</summary>
    public void Show(IReadOnlyList<AnalysisCall>? calls)
    {
        _calls = calls ?? [];
        _scale = Math.Max(1, _calls.Count == 0 ? 1 : _calls.Max(c => c.Context));
        _drawing = null;
        _hover = -1;
        if (_tip is not null) _tip.IsOpen = false;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        // Transparent, not nothing: the pointer is seen between the bars too.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        if (_calls.Count == 0 || size.Width < 10 || size.Height < Top + Bottom + 10) return;
        if (_drawing is null || _drawnFor != size)
        {
            _drawing = Draw(size);
            _drawnFor = size;
        }
        dc.DrawDrawing(_drawing);
        if (_hover >= 0 && _hover < _calls.Count)
        {
            var slot = size.Width / _calls.Count;
            dc.PushOpacity(0.16);
            dc.DrawRectangle(Palette.Text, null, new Rect(_hover * slot, 0, Math.Max(1, slot), size.Height - Bottom));
            dc.Pop();
        }
    }

    private DrawingGroup Draw(Size size)
    {
        var height = size.Height - Top - Bottom;
        var slot = size.Width / _calls.Count;
        var width = slot >= 3 ? slot * 0.72 : Math.Max(1, slot);
        var cached = new StreamGeometry { FillRule = FillRule.Nonzero };
        var fresh = new StreamGeometry { FillRule = FillRule.Nonzero };
        var rewritten = new StreamGeometry { FillRule = FillRule.Nonzero };
        var marks = new StreamGeometry { FillRule = FillRule.Nonzero };
        var pauses = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var c = cached.Open())
        using (var f = fresh.Open())
        using (var r = rewritten.Open())
        using (var m = marks.Open())
        using (var p = pauses.Open())
        {
            for (var i = 0; i < _calls.Count; i++)
            {
                var call = _calls[i];
                var x = i * slot + (slot - width) / 2;
                var bar = height * call.Context / _scale;
                var fromCache = height * call.CacheRead / _scale;
                var baseline = Top + height;
                if (call.Rewritten > 0) Box(r, x, baseline - bar, width, bar);
                else
                {
                    Box(c, x, baseline - fromCache, width, fromCache);
                    Box(f, x, baseline - bar, width, bar - fromCache);
                }
                if (call.Compaction)
                {
                    // A small triangle pointing down at the call that began from the summary.
                    var middle = x + width / 2;
                    m.BeginFigure(new Point(middle - 4, 0), true, true);
                    m.LineTo(new Point(middle + 4, 0), false, false);
                    m.LineTo(new Point(middle, Top - 2), false, false);
                }
                if (call.LongGap) Box(p, x, baseline + 2, Math.Max(1.5, width), Bottom - 2);
            }
        }

        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Palette.Faint, null, new Rect(0, Top + height, size.Width, 1));
            dc.DrawGeometry(Palette.Info, null, cached);
            dc.DrawGeometry(Palette.Muted, null, fresh);
            dc.DrawGeometry(Palette.Warn, null, rewritten);
            dc.DrawGeometry(Palette.Error, null, marks);
            dc.DrawGeometry(Palette.Faint, null, pauses);
        }
        group.Freeze();
        return group;
    }

    private static void Box(StreamGeometryContext g, double x, double y, double width, double height)
    {
        if (height <= 0 || width <= 0) return;
        g.BeginFigure(new Point(x, y), true, true);
        g.LineTo(new Point(x + width, y), false, false);
        g.LineTo(new Point(x + width, y + height), false, false);
        g.LineTo(new Point(x, y + height), false, false);
    }

    // ------------------------------------------------------------------ pointing

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_calls.Count == 0 || ActualWidth <= 0) return;
        var at = e.GetPosition(this);
        var index = Math.Clamp((int)(at.X / (ActualWidth / _calls.Count)), 0, _calls.Count - 1);
        _tip ??= new ToolTip { Placement = PlacementMode.Relative, PlacementTarget = this };
        if (index != _hover)
        {
            _hover = index;
            _tip.Content = Describe(index);
            InvalidateVisual();
        }
        _tip.HorizontalOffset = at.X + 14;
        _tip.VerticalOffset = at.Y + 18;
        _tip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_tip is not null) _tip.IsOpen = false;
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }

    private string Describe(int index)
    {
        var call = _calls[index];
        var text = new StringBuilder()
            .Append($"第 {index + 1} 次调用　·　{AnalysisText.When(call.At)}　·　{call.Model}\n")
            .Append($"上下文 {AnalysisText.Tokens(call.Context)}：缓存读取 {AnalysisText.Tokens(call.CacheRead)}　")
            .Append($"缓存写入 {AnalysisText.Tokens(call.CacheWrite)}　输入 {AnalysisText.Tokens(call.Input)}\n")
            .Append($"输出 {AnalysisText.Tokens(call.Output)}");
        if (call.Gap is { } gap)
            text.Append($"\n距上一次 {AnalysisText.Pause(gap)}").Append(call.LongGap ? "，超过了缓存保留的时间" : "");
        if (call.Rewritten > 0) text.Append($"\n缓存已过期：整段上下文重来了 {AnalysisText.Tokens(call.Rewritten)}");
        if (call.Compaction)
            text.Append(call.MarkedCompaction ? "\n在这之前压缩了（应用记下的）" : "\n在这之前压缩了（按上下文突然减半判断）");
        return text.ToString();
    }
}
