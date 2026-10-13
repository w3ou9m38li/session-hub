using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.App;

/// <summary>
/// Asks for the group to put the selected conversations in.
///
/// <para>Existing groups are listed rather than only offered as autocomplete, because the
/// failure mode that matters is a second group that differs from the first by a space or a
/// character — seeing the real list is what prevents it, and the name is normalised on the
/// way out so two that look identical cannot both exist. Typing filters the list, and a line
/// under the box says whether Enter will join an existing group or start a new one.</para>
/// </summary>
public partial class GroupDialog : Window
{
    private readonly IReadOnlyList<GroupTally> _groups;

    /// <summary>Set while a list click fills the box, so that text change does not re-filter the list under the click.</summary>
    private bool _fillingFromList;

    /// <summary>The cleaned group name, set only when the dialog was accepted.</summary>
    public string? GroupName { get; private set; }

    public GroupDialog(IReadOnlyList<GroupTally> existingGroups, int conversationCount, string? current = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _groups = existingGroups.Where(g => g.Name is not null).ToList();

        Title = conversationCount == 1 ? "设为分组" : $"设为分组（{conversationCount} 条会话）";
        PromptLine.Text = $"把选中的 {conversationCount} 条会话放入分组。输入新名字，或从下面挑一个已有的分组。"
            + "这是本工具自己的分组，Claude / ChatGPT 的侧边栏不受影响。";

        NameBox.Text = current ?? string.Empty;
        ApplyFilter();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_fillingFromList) ApplyFilter();
        UpdateOutcome();
    }

    /// <summary>Shows the groups whose name contains what was typed, case and width folded like search.</summary>
    private void ApplyFilter()
    {
        var typed = SearchKeys.Normalize(NameBox.Text);
        var visible = typed.Length == 0
            ? _groups
            : _groups.Where(g => SearchKeys.Normalize(g.Name).Contains(typed, StringComparison.Ordinal)).ToList();
        ExistingList.ItemsSource = visible;

        ExistingLine.Text = _groups.Count == 0
            ? "还没有任何分组：在上面的框里输入一个名字，就会新建"
            : visible.Count == _groups.Count
                ? $"已有 {_groups.Count} 个分组（点一下填入，双击直接使用）"
                : $"已有 {_groups.Count} 个分组，其中 {visible.Count} 个包含「{NameBox.Text.Trim()}」";
        UpdateOutcome();
    }

    private void UpdateOutcome()
    {
        var name = GroupRules.Normalize(NameBox.Text);
        OkButton.IsEnabled = name is not null;
        if (name is null)
        {
            OutcomeLine.Text = "输入分组名";
            OutcomeLine.Foreground = (System.Windows.Media.Brush)FindResource("FaintBrush");
            return;
        }

        var existing = _groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.Ordinal));
        var lookAlike = existing is null
            ? _groups.FirstOrDefault(g => string.Compare(g.Name, name, CultureInfo.CurrentCulture,
                  CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth) == 0)
            : null;

        (OutcomeLine.Text, OutcomeLine.Foreground) = existing is not null
            ? ($"放入已有分组「{name}」（现有 {existing.Count} 条）", (System.Windows.Media.Brush)FindResource("SuccessBrush"))
            : lookAlike is not null
                ? ($"将新建分组「{name}」——注意已有一个写法相近的「{lookAlike.Name}」",
                    (System.Windows.Media.Brush)FindResource("WarnBrush"))
                : ($"将新建分组「{name}」", (System.Windows.Media.Brush)FindResource("InfoBrush"));
    }

    private void ExistingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExistingList.SelectedItem is not GroupTally { Name: { } name }) return;
        _fillingFromList = true;
        try
        {
            NameBox.Text = name;
            NameBox.CaretIndex = name.Length;
        }
        finally
        {
            _fillingFromList = false;
        }
    }

    private void ExistingList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExistingList.SelectedItem is GroupTally) Accept();
    }

    private void ExistingList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ExistingList.SelectedItem is not GroupTally) return;
        Accept();
        e.Handled = true;
    }

    /// <summary>Down arrow walks from the box into the list, as in any picker.</summary>
    private void NameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || ExistingList.Items.Count == 0) return;
        ExistingList.SelectedIndex = Math.Max(0, ExistingList.SelectedIndex);
        (ExistingList.ItemContainerGenerator.ContainerFromIndex(ExistingList.SelectedIndex) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        var normalized = GroupRules.Normalize(NameBox.Text);
        if (normalized is null)
        {
            // Refusing here rather than creating an unnamed group: the list column would
            // show nothing and the group could never be picked again.
            OutcomeLine.Text = "分组名不能为空";
            NameBox.Focus();
            return;
        }

        GroupName = normalized;
        DialogResult = true;
    }
}
