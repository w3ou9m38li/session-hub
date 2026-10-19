using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeLauncher.App;

/// <summary>
/// The recent searches under the search box. It only shows them and says what was chosen: the
/// window keeps the list, and the keyboard stays in the search box, which moves the highlight
/// here — so typing on is never interrupted.
/// </summary>
public partial class SearchHistoryPanel : UserControl
{
    private readonly ObservableCollection<Entry> _entries = [];
    private int _highlight = -1;

    public SearchHistoryPanel()
    {
        InitializeComponent();
        EntryList.ItemsSource = _entries;
    }

    /// <summary>A search was picked, by click or Enter.</summary>
    public event Action<string>? Picked;

    /// <summary>A search is to leave the list, by its × or Delete.</summary>
    public event Action<string>? Removed;

    /// <summary>「全部清除」.</summary>
    public event Action? ClearedAll;

    /// <summary>The highlighted search, for Enter and Delete; null when none is.</summary>
    public string? Highlighted => _highlight >= 0 ? _entries[_highlight].Text : null;

    /// <param name="highlight">The row to highlight: after a removal, the one that took its place.</param>
    public void Show(IReadOnlyList<string> searches, int highlight = -1)
    {
        _entries.Clear();
        foreach (var search in searches) _entries.Add(new Entry(search));
        _highlight = -1;
        if (highlight >= 0 && _entries.Count > 0) Highlight(Math.Min(highlight, _entries.Count - 1));
    }

    /// <summary>
    /// ↓ from nothing highlighted goes to the newest, ↑ to the oldest; ↑ from the newest leaves
    /// the list, back to the search box. Returns the row now highlighted, -1 for none.
    /// </summary>
    public int MoveHighlight(int step)
    {
        if (_entries.Count == 0) return -1;
        var next = _highlight < 0
            ? (step > 0 ? 0 : _entries.Count - 1)
            : Math.Min(_highlight + step, _entries.Count - 1);
        Highlight(next);
        return _highlight;
    }

    public int HighlightedIndex => _highlight;

    private void Highlight(int index)
    {
        if (_highlight >= 0 && _highlight < _entries.Count) _entries[_highlight].IsHighlighted = false;
        _highlight = index;
        if (index >= 0) _entries[index].IsHighlighted = true;
    }

    private void Row_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Entry entry }) Highlight(_entries.IndexOf(entry));
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Entry entry }) Picked?.Invoke(entry.Text);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Entry entry }) Removed?.Invoke(entry.Text);
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e) => ClearedAll?.Invoke();

    /// <summary>One row: the search, and whether it is the one Enter or Delete acts on.</summary>
    public sealed class Entry(string text) : INotifyPropertyChanged
    {
        public string Text { get; } = text;

        public string Hint => "再搜一次「" + Text + "」";

        public string RemoveName => "从搜索历史里删掉「" + Text + "」";

        private bool _isHighlighted;

        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted == value) return;
                _isHighlighted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
