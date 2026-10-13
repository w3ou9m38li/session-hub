using System.Globalization;

namespace ClaudeLauncher.Core.Display;

/// <summary>
/// "When was this" in the words a person would use.
///
/// <para>A list of a few hundred conversations is scanned, not read: "昨天 21:40" is found at a
/// glance where "2026-09-22 21:40" has to be parsed digit by digit. The exact timestamp is never
/// lost — <see cref="Full"/> is what the tooltip and the detail panel show next to it.</para>
/// </summary>
public static class RelativeTime
{
    private static readonly string[] Weekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <param name="when">When it happened, in local time.</param>
    /// <param name="now">Local time now. Passed in rather than read so the wording is testable.</param>
    public static string Format(DateTime when, DateTime now)
    {
        var elapsed = now - when;

        // A timestamp slightly ahead of this clock (another machine, a file synced in) is
        // still "just now". One far in the future is bad data, and a relative phrase would
        // hide that, so it is shown as it stands.
        if (elapsed < TimeSpan.FromMinutes(-2)) return when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (elapsed < TimeSpan.FromMinutes(1)) return "刚刚";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} 分钟前";

        var clock = when.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (now.Date - when.Date).Days;
        if (days == 0) return $"今天 {clock}";
        if (days == 1) return $"昨天 {clock}";
        if (days < 7) return $"{Weekdays[(int)when.DayOfWeek]} {clock}";
        if (when.Year == now.Year) return when.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        return when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>The unabridged form, with the weekday that the short forms sometimes stand in for.</summary>
    public static string Full(DateTime when) =>
        when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + $"（{Weekdays[(int)when.DayOfWeek]}）";
}
