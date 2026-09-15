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
        // The bars and lock dot are white for a clean, mac-like look on the dark pill; the success
        // check keeps the accent color as a small moment of life.
        var white = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));
        white.Freeze();
        Bars.Brush = white;
        LockDot.Fill = white;
        Check.Foreground = new SolidColorBrush(Theme.AccentColor(App.Settings.Current.Accent));
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
                SetContent(bars: 0.55);
                ShowPill();
                break;
            case SessionState.Listening:
                Bars.Mode = BarsMode.Live;
                SetContent(bars: 1);
                ShowPill();
                break;
            case SessionState.HandsFree:
                Bars.Mode = BarsMode.Live;
                SetContent(bars: 1, lockDot: true);
                ShowPill();
                break;
            case SessionState.Processing:
                Bars.Mode = BarsMode.Processing;
                SetContent(bars: 1);
                ShowPill();
                break;
            case SessionState.Done:
                SetContent(check: true);
                ShowPill();
                break;
            case SessionState.Notice:
                Message.Text = message ?? "";
                SetContent(message: true);
                ShowPill();
                break;
            default:
                HidePill();
                break;
        }
    }

    /// <summary>
    /// Cross-fades the pill's contents to an explicit target. Every element is animated (never direct-set)
    /// so a held animation can't shadow a later value — that was the bug that made the bars vanish.
    /// </summary>
    private void SetContent(double bars = 0, bool lockDot = false, bool check = false, bool message = false)
    {
        Fade(Bars, bars, 130);
        Fade(LockDot, lockDot ? 1 : 0, 160);
        Fade(Check, check ? 1 : 0, 160);
        Fade(Message, message ? 1 : 0, 140);
        if (check)
        {
            var pop = new DoubleAnimation(0.6, 1, new Duration(TimeSpan.FromMilliseconds(280)))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 },
            };
            CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        // Size the pill to what it shows: bars are fixed width, messages measure their text.
        double target = 128;
        if (message)
        {
            Message.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            target = Math.Max(128, Message.DesiredSize.Width + Pill.Padding.Left + Pill.Padding.Right + 2);
        }
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
        // Re-place even when already shown: the target app may be on another monitor now.
        Place();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (_shown) return;
        _shown = true;

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
            // The window stays visible-but-transparent; only the render loop stops so idle costs nothing.
            // Content opacities are left as animations; the next show re-animates them to their targets.
            if (_rendering)
            {
                _rendering = false;
                CompositionTarget.Rendering -= OnRendering;
            }
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
        // Animate the bars in every state that shows them; using the state (not the animated opacity)
        // keeps them ticking reliably across repeated dictations.
        if (_state is SessionState.Arming or SessionState.Listening or SessionState.HandsFree or SessionState.Processing)
            Bars.Tick(dt);
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
