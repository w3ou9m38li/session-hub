using System.Diagnostics;

namespace ClaudeLauncher.App;

/// <summary>
/// Progress from a background pass, passed on to the window at most every quarter of a second —
/// and always the last report. A pass over thousands of transcripts reports once per file; posted
/// to the window each time, those reports alone would keep it busy.
/// </summary>
internal sealed class ThrottledProgress<T> : IProgress<T>
{
    private static readonly long Interval = Stopwatch.Frequency / 4;

    private readonly Progress<T> _window;
    private readonly Func<T, bool> _isLast;
    private long _lastPosted;

    /// <param name="handler">Run on the thread that made this — the window's.</param>
    /// <param name="isLast">Whether a report is the last of the pass: it is always passed on.</param>
    public ThrottledProgress(Action<T> handler, Func<T, bool> isLast)
    {
        _window = new Progress<T>(handler);
        _isLast = isLast;
    }

    public void Report(T value)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastPosted);
        if (!_isLast(value) && now - last < Interval) return;
        Interlocked.Exchange(ref _lastPosted, now);
        ((IProgress<T>)_window).Report(value);
    }
}
