using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Disper.Core;

namespace Disper.Dashboard;

public partial class HomePage : UserControl, IDashboardPage
{
    private DispatcherTimer? _meterTimer;
    private bool _testing;
    private double _meter;

    public HomePage()
    {
        InitializeComponent();
        Unloaded += (_, _) => StopTest();
    }

    public void OnShow()
    {
        var s = App.Settings.Current;
        Scaffold.Subtitle = Greeting(s.UserName);
        KeyCap.Text = HotkeyService.LabelFor(s.Hotkey);
        HotkeyHint.Text = "Hold your key anywhere, speak, and let go. Your words drop into whatever you're typing in.";

        var stats = App.History.ComputeStats();
        Animate(WordsToday, stats.WordsToday);
        Animate(WordsTotal, stats.WordsTotal);
        TimeSaved.Text = FormatMinutes(stats.MinutesSaved);
        Animate(Streak, stats.StreakDays);
        StreakFlame.Visibility = stats.StreakDays > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Greeting(string name)
    {
        var h = DateTime.Now.Hour;
        var part = h < 5 ? "Hello" : h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : h < 22 ? "Good evening" : "Still up";
        return $"{part}, {name}.";
    }

    private static string FormatMinutes(double minutes)
    {
        if (minutes < 1) return "0m";
        if (minutes < 60) return $"{Math.Round(minutes)}m";
        var h = minutes / 60.0;
        return h < 10 ? $"{h:0.#}h" : $"{Math.Round(h)}h";
    }

    /// <summary>Counts a stat up from its current value so the number feels earned, not just stamped in.</summary>
    private static void Animate(TextBlock target, int to)
    {
        int from = int.TryParse(target.Text.Replace(",", ""), out var v) ? v : 0;
        if (from == to)
        {
            target.Text = to.ToString("N0");
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => { }, target.Dispatcher);
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / 500.0);
            double eased = 1 - Math.Pow(1 - t, 3);
            target.Text = ((int)Math.Round(from + (to - from) * eased)).ToString("N0");
            if (t >= 1) timer.Stop();
        };
        timer.Start();
    }

    private void OnTestMic(object sender, RoutedEventArgs e)
    {
        if (_testing) StopTest();
        else StartTest();
    }

    private void StartTest()
    {
        _testing = true;
        TestBtn.Content = "Stop test";
        MeterCard.Visibility = Visibility.Visible;
        MeterCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150))));
        App.Audio.Level += OnLevel;
        App.Audio.Start();
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            double target = _meter * (MeterCard.ActualWidth - 48);
            double now = MeterFill.Width;
            MeterFill.Width = Math.Max(0, now + (target - now) * 0.3);
        }, Dispatcher);
        _meterTimer.Start();
    }

    private void StopTest()
    {
        if (!_testing) return;
        _testing = false;
        TestBtn.Content = "Test microphone";
        App.Audio.Level -= OnLevel;
        Task.Run(() => App.Audio.Stop());
        _meterTimer?.Stop();
        MeterFill.Width = 0;
        MeterCard.Visibility = Visibility.Collapsed;
    }

    private void OnLevel(float level) => _meter = level;
}
