using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Disper.Core;

namespace Disper.Dashboard;

public partial class SnippetsPage : UserControl, IDashboardPage
{
    public sealed record Row(int Index, string Trigger, string Preview);

    private readonly ObservableCollection<Row> _rows = new();

    public SnippetsPage()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
    }

    public void OnShow() => Refresh();

    private void Refresh()
    {
        _rows.Clear();
        var snippets = App.Settings.Current.Snippets;
        for (int i = 0; i < snippets.Count; i++)
            _rows.Add(new Row(i, snippets[i].Trigger, snippets[i].Text.Replace("\r", " ").Replace("\n", " ")));
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTriggerKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TextBoxContent.Focus();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var trigger = TriggerBox.Text.Trim();
        var text = TextBoxContent.Text.Trim();
        if (trigger.Length == 0) { TriggerBox.Focus(); return; }
        if (text.Length == 0) { TextBoxContent.Focus(); return; }
        App.Settings.Update(s => s.Snippets.Add(new Snippet { Trigger = trigger, Text = text }));
        TriggerBox.Text = "";
        TextBoxContent.Text = "";
        TriggerBox.Focus();
        Refresh();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        int index = (int)((Button)sender).Tag;
        App.Settings.Update(s =>
        {
            if (index >= 0 && index < s.Snippets.Count) s.Snippets.RemoveAt(index);
        });
        Refresh();
    }
}
