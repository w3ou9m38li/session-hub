using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.App.ViewModels;

/// <summary>
/// One entry of the group filter: what it reads as, and the filter it applies.
///
/// <para>The ungrouped bucket is a real entry rather than an absence, so "show me the ones I
/// have not sorted yet" is one click instead of something the user has to work out.</para>
/// </summary>
public sealed class GroupOptionViewModel(GroupFilter filter, string display)
{
    public GroupFilter Filter { get; } = filter;
    public string Display { get; } = display;

    public override string ToString() => Display;
}

/// <summary>
/// One entry of the sort picker, and how its two directions read. The words matter more than
/// the arrow: "新→旧" and "Claude 在前" say what an arrow can only hint at.
/// </summary>
public sealed class SortOptionViewModel(SortField field, string display, string natural, string reversed)
{
    public SortField Field { get; } = field;
    public string Display { get; } = display;

    public string Direction(bool isReversed) => isReversed ? reversed : natural;

    public override string ToString() => Display;

    public static IReadOnlyList<SortOptionViewModel> All { get; } =
    [
        new(SortField.LastActivity, "最后活动", "新→旧", "旧→新"),
        new(SortField.Title, "标题", "A→Z", "Z→A"),
        new(SortField.Group, "分组", "A→Z", "Z→A"),
        new(SortField.Source, "来源", "Claude 在前", "ChatGPT 在前"),
        new(SortField.Project, "工作目录", "A→Z", "Z→A"),
        new(SortField.AppState, "应用内状态", "活跃在前", "活跃在后"),
        new(SortField.Size, "大小", "大→小", "小→大"),
    ];

    /// <summary>Only a search has one: the words in the title before those only said, then the most mentions.</summary>
    public static SortOptionViewModel Relevance { get; } = new(SortField.Relevance, "相关度", "最相关在前", "最相关在后");

    /// <summary>The orders offered while searching: most relevant first, then the list's own.</summary>
    public static IReadOnlyList<SortOptionViewModel> Searching { get; } = [Relevance, .. All];

    public static SortOptionViewModel For(SortField field) => Searching.FirstOrDefault(o => o.Field == field) ?? All[0];
}

/// <summary>How far back the list reaches, counted in whole local days including today.</summary>
public enum TimeWindow { All, Today, Week, Month, Quarter }

/// <summary>One entry of the time filter.</summary>
public sealed class TimeOptionViewModel(TimeWindow window, string display, int days)
{
    public TimeWindow Window { get; } = window;
    public string Display { get; } = display;

    /// <summary>Local midnight <c>days - 1</c> days ago; null for no limit.</summary>
    public DateTimeOffset? Since(DateTime today) =>
        days <= 0 ? null : new DateTimeOffset(DateTime.SpecifyKind(today.Date.AddDays(1 - days), DateTimeKind.Local));

    public override string ToString() => Display;

    public static IReadOnlyList<TimeOptionViewModel> All { get; } =
    [
        new(TimeWindow.All, "全部时间", 0),
        new(TimeWindow.Today, "今天", 1),
        new(TimeWindow.Week, "最近 7 天", 7),
        new(TimeWindow.Month, "最近 30 天", 30),
        new(TimeWindow.Quarter, "最近 90 天", 90),
    ];
}

/// <summary>One entry of the app filter: all conversations, or one app family's (Claude's, ChatGPT's…).</summary>
public sealed class BackendOptionViewModel(BackendFilter filter, string display)
{
    public BackendFilter Filter { get; } = filter;
    public string Display { get; } = display;

    public override string ToString() => Display;

    public static IReadOnlyList<BackendOptionViewModel> All { get; } =
    [
        new(BackendFilter.All, "全部应用"),
        .. Sources.Families.Select(f => new BackendOptionViewModel(new BackendFilter(f.Family), f.FilterName)),
    ];
}
