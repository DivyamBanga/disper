using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Disper.Core;

namespace Disper.Dashboard;

public partial class DictionaryPage : UserControl, IDashboardPage
{
    public sealed record Row(int Index, string Word, string Sounds, Visibility SoundsVisible);

    private readonly ObservableCollection<Row> _rows = new();

    public DictionaryPage()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
    }

    public void OnShow() => Refresh();

    private void Refresh()
    {
        _rows.Clear();
        var dict = App.Settings.Current.Dictionary;
        for (int i = 0; i < dict.Count; i++)
        {
            var d = dict[i];
            var sounds = string.IsNullOrWhiteSpace(d.SoundsLike) ? "" : $"sounds like {d.SoundsLike}";
            _rows.Add(new Row(i, d.Word, sounds, sounds.Length == 0 ? Visibility.Collapsed : Visibility.Visible));
        }
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnAdd(sender, e);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var word = WordBox.Text.Trim();
        if (word.Length == 0) { WordBox.Focus(); return; }
        var sounds = SoundsBox.Text.Trim();
        App.Settings.Update(s => s.Dictionary.Add(new DictionaryEntry { Word = word, SoundsLike = sounds }));
        WordBox.Text = "";
        SoundsBox.Text = "";
        WordBox.Focus();
        Refresh();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        int index = (int)((Button)sender).Tag;
        App.Settings.Update(s =>
        {
            if (index >= 0 && index < s.Dictionary.Count) s.Dictionary.RemoveAt(index);
        });
        Refresh();
    }
}
