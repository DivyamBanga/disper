using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Disper.Core;

namespace Disper.Dashboard;

public partial class SettingsPage : UserControl, IDashboardPage
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        BuildAccentSwatches();

        foreach (var (id, label, _) in HotkeyService.Choices)
            HotkeyCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        HotkeyCombo.SelectionChanged += OnHotkeyChanged;

        foreach (var m in ModelCatalog.All)
            ModelCombo.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
        ModelCombo.SelectionChanged += OnModelChanged;

        foreach (var t in new[] { 2, 4, 6 })
            ThreadsCombo.Items.Add(new ComboBoxItem { Content = t.ToString(), Tag = t });
        ThreadsCombo.SelectionChanged += OnThreadsChanged;

        foreach (var (id, name, _) in SoundStyles.All)
            SoundStyleCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        SoundStyleCombo.SelectionChanged += OnSoundStyleChanged;

        MicCombo.SelectionChanged += OnMicChanged;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Disper {v?.Major}.{v?.Minor}.{v?.Build} · fully offline";
    }

    public void OnShow()
    {
        _loading = true;
        var s = App.Settings.Current;

        Select(HotkeyCombo, s.Hotkey);
        TapToggle.IsChecked = s.TapThresholdMs > 0;
        SegPaste.IsChecked = s.InsertionMode == InsertionMode.Paste;
        SegType.IsChecked = s.InsertionMode == InsertionMode.Type;
        FillerToggle.IsChecked = s.RemoveFillers;
        HistoryToggle.IsChecked = s.SaveHistory;
        SoundToggle.IsChecked = s.SoundCues;
        Select(SoundStyleCombo, s.SoundStyle);
        StartupToggle.IsChecked = s.StartAtLogin;
        NameBox.Text = s.UserName;
        Select(ModelCombo, s.ModelId);
        UpdateModelDescription();
        Select(ThreadsCombo, s.Threads);
        HighlightAccent(s.Accent);
        LoadMics(s.MicrophoneId);

        _loading = false;
    }

    private void LoadMics(string selectedId)
    {
        MicCombo.Items.Clear();
        MicCombo.Items.Add(new ComboBoxItem { Content = "System default", Tag = "" });
        foreach (var d in AudioCapture.ListDevices())
            MicCombo.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        Select(MicCombo, selectedId);
    }

    private static void Select(ComboBox combo, object tag)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (Equals(item.Tag, tag)) { combo.SelectedItem = item; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void OnHotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (TagOf(HotkeyCombo) is { } id) App.Settings.Update(s => s.Hotkey = id);
    }

    private void OnTapToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Update(s => s.TapThresholdMs = TapToggle.IsChecked == true ? 250 : 0);
    }

    private void OnInsertionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Update(s => s.InsertionMode = SegType.IsChecked == true ? InsertionMode.Type : InsertionMode.Paste);
    }

    private void OnFillerToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Update(s => s.RemoveFillers = FillerToggle.IsChecked == true);
    }

    private void OnHistoryToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Update(s => s.SaveHistory = HistoryToggle.IsChecked == true);
    }

    private void OnSoundToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Update(s => s.SoundCues = SoundToggle.IsChecked == true);
        if (SoundToggle.IsChecked == true) App.Sounds?.Start();
    }

    private void OnSoundStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (TagOf(SoundStyleCombo) is { } id)
        {
            App.Settings.Update(s => s.SoundStyle = id);
            App.Sounds?.Preview(id);
        }
    }

    private void OnPreviewSound(object sender, RoutedEventArgs e)
    {
        if (TagOf(SoundStyleCombo) is { } id) App.Sounds?.Preview(id);
    }

    private void OnStartupToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Update(s => s.StartAtLogin = StartupToggle.IsChecked == true);
    }

    private void OnNameChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { name = "there"; NameBox.Text = name; }
        App.Settings.Update(s => s.UserName = name);
    }

    private void OnMicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (TagOf(MicCombo) is { } id)
        {
            App.Settings.Update(s => s.MicrophoneId = id);
            App.Instance.ReloadMicrophone();
        }
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModelDescription();
        if (_loading) return;
        if (TagOf(ModelCombo) is { } id && id != App.Settings.Current.ModelId)
        {
            App.Settings.Update(s => s.ModelId = id);
            _ = App.Instance.ReloadModelAsync();
        }
    }

    private void UpdateModelDescription()
    {
        if (TagOf(ModelCombo) is not { } id) return;
        var m = ModelCatalog.Get(id);
        var installed = m.IsInstalled ? "" : $" · downloads {m.SizeMb} MB on first use";
        ModelRow.Description = $"{m.Tagline}. Runs fully offline{installed}.";
    }

    private void OnThreadsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if ((ThreadsCombo.SelectedItem as ComboBoxItem)?.Tag is int t && t != App.Settings.Current.Threads)
        {
            App.Settings.Update(s => s.Threads = t);
            _ = App.Instance.ReloadModelAsync();
        }
    }

    // ---------- accent swatches ----------

    private void BuildAccentSwatches()
    {
        foreach (var (id, name, color) in Theme.Accents)
        {
            var ring = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(0, 0, 8, 0),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = name,
                Tag = id,
                Child = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(color) },
            };
            ring.MouseLeftButtonUp += (_, _) =>
            {
                App.Settings.Update(s => s.Accent = id);
                HighlightAccent(id);
            };
            AccentSwatches.Children.Add(ring);
        }
    }

    private void HighlightAccent(string id)
    {
        foreach (Border ring in AccentSwatches.Children)
            ring.BorderBrush = (string)ring.Tag == id ? (Brush)FindResource("B.Accent") : Brushes.Transparent;
    }
}
