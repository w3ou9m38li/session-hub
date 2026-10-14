using System.IO;
using System.Windows;
using ClaudeLauncher.Infrastructure.Backends;
using Microsoft.Win32;

namespace ClaudeLauncher.App;

/// <summary>
/// Where DeepSeek Harness's sessions are read from: the homes found on their own (DSH_HOME, ~\.dsh)
/// and the folders the user added, with how many sessions each holds. Adds and removes the
/// user's folders; the caller saves them and syncs.
/// </summary>
public partial class DshHomesDialog : Window
{
    /// <summary>One home in the list: where it is, how it was found, and whether the user can remove it.</summary>
    public sealed record HomeItem(string Path, string Note, bool Added)
    {
        public Visibility RemoveVisibility => Added ? Visibility.Visible : Visibility.Collapsed;
        public string RemoveName => "移除 " + Path;
    }

    private readonly List<string> _added;
    private bool _changed;

    private DshHomesDialog(IEnumerable<string> added)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _added = added.Select(f => DshPaths.HomeOf(f) ?? f).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Render();
    }

    /// <summary>The user's folders after the dialog, when they changed; null when nothing did.</summary>
    public static List<string>? Ask(Window owner, IEnumerable<string> added)
    {
        var dialog = new DshHomesDialog(added) { Owner = owner };
        dialog.ShowDialog();
        return dialog._changed ? dialog._added : null;
    }

    private void Render()
    {
        var found = DshPaths.Discover().Homes;
        var items = new List<HomeItem>();
        foreach (var home in _added)
            items.Add(new HomeItem(home, "你添加的 · " + Count(home), Added: true));
        foreach (var home in found.Where(h => !_added.Contains(h, StringComparer.OrdinalIgnoreCase)))
            items.Add(new HomeItem(home, "自动找到（DSH_HOME 或 .dsh）· " + Count(home), Added: false));
        HomeList.ItemsSource = items;
        EmptyLine.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Count(string home)
    {
        if (!Directory.Exists(Path.Combine(home, "sessions"))) return "文件夹现在不在";
        try
        {
            var sessions = new DshPaths([home]).SessionFiles().Count();
            return sessions == 0 ? "还没有会话" : $"{sessions} 个会话";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "读不了这个文件夹";
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择 DeepSeek Harness 的数据目录（或部署它的文件夹）" };
        if (picker.ShowDialog(this) != true || string.IsNullOrWhiteSpace(picker.FolderName)) return;
        if (DshPaths.HomeOf(picker.FolderName) is not { } home)
        {
            MessageDialog.Show(this, DialogKind.Warning, "这里没有 DeepSeek Harness 的会话",
                $"在「{picker.FolderName}」里没找到 sessions 文件夹，也没有 data\\sessions。\n\n"
                + "请选 DSH_HOME 指向的文件夹（里面有 sessions），或部署 DeepSeek Harness 的文件夹（里面有 data）。");
            return;
        }
        if (_added.Contains(home, StringComparer.OrdinalIgnoreCase))
        {
            StatusLine.Text = "这个文件夹已经在列表里了。";
            return;
        }
        _added.Add(home);
        _changed = true;
        StatusLine.Text = $"已添加：{home}。点「完成」后马上读取。";
        Render();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HomeItem { Added: true } item) return;
        _added.RemoveAll(f => string.Equals(f, item.Path, StringComparison.OrdinalIgnoreCase));
        _changed = true;
        StatusLine.Text = "已移除。列表里已有的这些会话照样保留；只是不再读这个文件夹的新会话。";
        Render();
    }
}
