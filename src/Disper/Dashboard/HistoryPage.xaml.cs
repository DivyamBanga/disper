using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Disper.Core;

namespace Disper.Dashboard;

public partial class HistoryPage : UserControl, IDashboardPage
{
    public sealed record Row(string Id, string Text, string Meta);

    private readonly ObservableCollection<Row> _rows = new();

    public HistoryPage()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        App.History.Changed += OnHistoryChanged;
        Unloaded += (_, _) => App.History.Changed -= OnHistoryChanged;
    }

    private void OnHistoryChanged() => Dispatcher.BeginInvoke(() => Refresh(SearchBox.Text));

    public void OnShow() => Refresh(SearchBox.Text);

    private void Refresh(string query)
    {
        query = query.Trim();
        var entries = App.History.Entries
            .OrderByDescending(e => e.Time)
            .Where(e => query.Length == 0 || e.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(500)
            .Select(e => new Row(e.Id, e.Text, $"{Ago(e.Time)} · {e.Words} words{AppSuffix(e.App)}"))
            .ToList();

        _rows.Clear();
        foreach (var r in entries) _rows.Add(r);

        bool empty = _rows.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = App.History.Entries.Count == 0 ? "Nothing dictated yet." : "No matches.";
        Scaffold.Subtitle = App.History.Entries.Count switch
        {
            0 => "Your dictations will show up here.",
            1 => "1 dictation.",
            var n => $"{n:N0} dictations.",
        };
        ClearBtn.Visibility = App.History.Entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string AppSuffix(string app) => string.IsNullOrEmpty(app) ? "" : $" · {app}";

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24 && t.Date == DateTime.Today) return t.ToString("h:mm tt").ToLowerInvariant();
        if (t.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        if (d.TotalDays < 7) return t.ToString("ddd h:mm tt").ToLowerInvariant();
        return t.ToString("MMM d");
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => Refresh(SearchBox.Text);

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        var entry = App.History.Entries.FirstOrDefault(x => x.Id == id);
        if (entry is not null)
        {
            try { Clipboard.SetText(entry.Text); } catch { }
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        App.History.Delete(id);
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        var confirm = new ConfirmDialog("Clear all history?", "This permanently deletes every saved dictation.", "Clear all")
        {
            Owner = Window.GetWindow(this),
        };
        if (confirm.ShowDialog() == true) App.History.Clear();
    }
}
