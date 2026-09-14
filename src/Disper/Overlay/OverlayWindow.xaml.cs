using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Disper.Core;

namespace Disper.Overlay;

/// <summary>
/// The bottom-center pill. A layered, click-through, non-activating tool window that is created once and
/// shown/hidden with short animations. It never takes focus, so the app being dictated into keeps it.
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly Duration EnterDuration = new(TimeSpan.FromMilliseconds(170));
    private static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(130));
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    private readonly DictationController _controller;
    private nint _hwnd;
    private bool _shown;
    private bool _rendering;
    private TimeSpan _lastRender;
    private SessionState _state = SessionState.Idle;

    public OverlayWindow(DictationController controller)
    {
        InitializeComponent();
        _controller = controller;
        _controller.StateChanged += OnStateChanged;
        _controller.Level += level => Bars.TargetLevel = level;
        ApplyAccent();
    }

    public void ApplyAccent()
    {
        var color = Theme.AccentColor(App.Settings.Current.Accent);
        Bars.Brush = new SolidColorBrush(color);
        LockDot.Fill = new SolidColorBrush(color);
        Check.Foreground = new SolidColorBrush(color);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST;
        ex &= ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new nint(ex));
    }

    private void OnStateChanged(SessionState state, string? message)
    {
        _state = state;
        switch (state)
        {
            case SessionState.Arming:
                Bars.Mode = BarsMode.Arming;
                Bars.Opacity = 0.45;
                ShowContent(bars: true);
                ShowPill();
                break;
            case SessionState.Listening:
                Bars.Mode = BarsMode.Live;
                Fade(Bars, 1, 120);
                ShowContent(bars: true);
                ShowPill();
                break;
            case SessionState.HandsFree:
                Bars.Mode = BarsMode.Live;
                Fade(Bars, 1, 120);
                ShowContent(bars: true, lockDot: true);
                ShowPill();
                break;
            case SessionState.Processing:
                Bars.Mode = BarsMode.Processing;
                Fade(Bars, 0.85, 150);
                ShowContent(bars: true);
                ShowPill();
                break;
            case SessionState.Done:
                ShowContent(check: true);
                ShowPill();
                break;
            case SessionState.Notice:
                Message.Text = message ?? "";
                ShowContent(message: true);
                ShowPill();
                break;
            default:
                HidePill();
                break;
        }
    }

    private void ShowContent(bool bars = false, bool message = false, bool check = false, bool lockDot = false)
    {
        Fade(Bars, bars ? Bars.Opacity : 0, 120);
        if (!bars) Bars.Opacity = 0;
        Fade(Message, message ? 1 : 0, 140);
        Fade(LockDot, lockDot ? 1 : 0, 160);
        Fade(Check, check ? 1 : 0, 160);
        if (check)
        {
            var pop = new DoubleAnimation(0.6, 1, new Duration(TimeSpan.FromMilliseconds(260)))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
            };
            CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        // Size the pill to what it shows: bars are fixed width, messages measure their text.
        double target;
        if (message)
        {
            Message.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            target = Math.Max(128, Message.DesiredSize.Width + Pill.Padding.Left + Pill.Padding.Right + 2);
        }
        else target = 128;
        AnimateWidth(target);
    }

    private void AnimateWidth(double target)
    {
        double from = double.IsNaN(Pill.Width) ? Pill.ActualWidth : Pill.Width;
        if (from <= 0) from = target;
        if (Math.Abs(from - target) < 0.5)
        {
            Pill.BeginAnimation(WidthProperty, null);
            Pill.Width = target;
            return;
        }
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(from, target, new Duration(TimeSpan.FromMilliseconds(180))) { EasingFunction = EaseOut });
    }

    private static void Fade(UIElement element, double to, int ms)
    {
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = EaseOut });
    }

    private void ShowPill()
    {
        if (_shown) return;
        _shown = true;
        Place();
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, EnterDuration) { EasingFunction = EaseOut });
        PillShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, EnterDuration) { EasingFunction = EaseOut });
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, EnterDuration) { EasingFunction = EaseOut });
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, EnterDuration) { EasingFunction = EaseOut });

        if (!_rendering)
        {
            _rendering = true;
            _lastRender = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void HidePill()
    {
        if (!_shown) return;
        _shown = false;
        var fade = new DoubleAnimation(0, ExitDuration) { EasingFunction = EaseIn };
        fade.Completed += (_, _) =>
        {
            if (_shown) return;
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
            if (_rendering)
            {
                _rendering = false;
                CompositionTarget.Rendering -= OnRendering;
            }
            Bars.Opacity = 0;
            Message.Opacity = 0;
            Check.Opacity = 0;
            LockDot.Opacity = 0;
        };
        Pill.BeginAnimation(OpacityProperty, fade);
        PillShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, ExitDuration) { EasingFunction = EaseIn });
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, ExitDuration) { EasingFunction = EaseIn });
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, ExitDuration) { EasingFunction = EaseIn });
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastRender == TimeSpan.Zero ? 1 / 60.0 : (now - _lastRender).TotalSeconds;
        _lastRender = now;
        if (Bars.Opacity > 0) Bars.Tick(dt);
    }

    /// <summary>Bottom-center of the monitor that holds the window being dictated into, in physical pixels.</summary>
    private void Place()
    {
        var target = Native.GetForegroundWindow();
        nint monitor = target != 0
            ? Native.MonitorFromWindow(target, Native.MONITOR_DEFAULTTONEAREST)
            : Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY);

        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfoW(monitor, ref info)) return;
        uint dpi = 96;
        if (Native.GetDpiForMonitor(monitor, 0, out var dx, out _) == 0) dpi = dx;
        double scale = dpi / 96.0;

        int w = (int)Math.Round(Width * scale);
        int h = (int)Math.Round(Height * scale);
        var work = info.rcWork;
        int x = (work.Left + work.Right) / 2 - w / 2;
        int y = work.Bottom - h - (int)Math.Round(4 * scale);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
    }
}
