using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Disper.Core;

namespace Disper.Dashboard;

public partial class DashboardWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private string _current = "";

    public DashboardWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Navigate("Home");
            UpdateStatus();
            var shot = Environment.GetEnvironmentVariable("DISPER_SHOT");
            if (!string.IsNullOrEmpty(shot))
            {
                // Wait until the model is ready (or 8 s) so the status shows its real state in the shot.
                var start = DateTime.UtcNow;
                var t = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(200), System.Windows.Threading.DispatcherPriority.ApplicationIdle, (s, _) =>
                {
                    if (App.Transcriber.IsReady || (DateTime.UtcNow - start).TotalSeconds > 8)
                    {
                        ((System.Windows.Threading.DispatcherTimer)s!).Stop();
                        UpdateStatus();
                        ShootAll(shot);
                    }
                }, Dispatcher);
                t.Start();
            }
        };
        App.Transcriber.StateChanged += OnEngineStateChanged;
        App.Instance.SetupProgress += OnSetupProgress;
        Closed += (_, _) =>
        {
            App.Transcriber.StateChanged -= OnEngineStateChanged;
            App.Instance.SetupProgress -= OnSetupProgress;
        };
    }

    private void OnEngineStateChanged() => Dispatcher.BeginInvoke(UpdateStatus);
    private void OnSetupProgress(DownloadProgress? p) => Dispatcher.BeginInvoke(UpdateStatus);

    private void UpdateStatus()
    {
        var setup = App.Instance.CurrentSetup;
        if (App.Transcriber.IsReady)
        {
            StatusText.Text = $"Ready · hold {HotkeyService.LabelFor(App.Settings.Current.Hotkey)}";
            StatusDot.Fill = (Brush)FindResource("B.Accent");
        }
        else if (setup is not null)
        {
            StatusText.Text = setup.Status;
            StatusDot.Fill = (Brush)FindResource("B.TextTertiary");
        }
        else if (App.Transcriber.LoadError is not null)
        {
            StatusText.Text = "Model failed to load";
            StatusDot.Fill = (Brush)FindResource("B.Danger");
        }
        else
        {
            StatusText.Text = "Loading model…";
            StatusDot.Fill = (Brush)FindResource("B.TextTertiary");
        }
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var rb = (System.Windows.Controls.RadioButton)sender;
        var name = rb.Name.Replace("Nav", "");
        Navigate(name);
    }

    private void Navigate(string name)
    {
        if (_current == name) return;
        _current = name;

        if (!_pages.TryGetValue(name, out var page))
        {
            page = name switch
            {
                "Home" => new HomePage(),
                "History" => new HistoryPage(),
                "Dictionary" => new DictionaryPage(),
                "Snippets" => new SnippetsPage(),
                "Settings" => new SettingsPage(),
                _ => new HomePage(),
            };
            _pages[name] = page;
        }

        // Quick cross-fade with a small upward slide.
        var tt = new TranslateTransform(0, 8);
        page.RenderTransform = tt;
        page.Opacity = 0;
        PageHost.Content = page;

        // Apply templates before the page populates its controls, so setting e.g. a toggle's IsChecked
        // can resolve the templated parts it animates instead of throwing.
        page.UpdateLayout();
        if (page is IDashboardPage refreshable) refreshable.OnShow();

        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(180))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Test-only: render every page to a PNG in <paramref name="dir"/> so screenshots don't depend on desktop compositing.</summary>
    private async void ShootAll(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (nav, name) in new[] { (NavHome, "home"), (NavHistory, "history"), (NavDictionary, "dictionary"), (NavSnippets, "snippets"), (NavSettings, "settings") })
        {
            _current = "";
            nav.IsChecked = true;
            Navigate(char.ToUpper(name[0]) + name[1..]);
            if (PageHost.Content is UIElement page)
            {
                page.BeginAnimation(OpacityProperty, null);
                page.Opacity = 1;
                page.RenderTransform = System.Windows.Media.Transform.Identity;
            }
            await Task.Delay(300);   // let toggle slides and cross-fades settle before capturing
            UpdateLayout();
            SelfShot(Path.Combine(dir, $"page_{name}.png"));
        }
        Core.Log.Info("shootall done");
    }

    /// <summary>Test-only: render the window to a PNG so screenshots don't depend on desktop compositing.</summary>
    private void SelfShot(string path)
    {
        try
        {
            var dpi = 96 * (VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var w = (int)ActualWidth;
            var h = (int)ActualHeight;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * dpi / 96), (int)(h * dpi / 96), dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(this);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
            Core.Log.Info($"selfshot saved {path} ({w}x{h})");
        }
        catch (Exception ex) { Core.Log.Error("selfshot failed", ex); }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Hide(); // keep running in the tray
}

/// <summary>A page that wants a callback each time it becomes visible (to refresh from stores).</summary>
public interface IDashboardPage
{
    void OnShow();
}
