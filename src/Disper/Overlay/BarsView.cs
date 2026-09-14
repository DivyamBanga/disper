using System.Windows;
using System.Windows.Media;

namespace Disper.Overlay;

public enum BarsMode { Arming, Live, Processing }

/// <summary>
/// The audio-reactive bars. Drawn directly in OnRender each frame (nine rounded rectangles), so a frame costs
/// almost nothing. Heights follow a smoothed input level with a slow travelling wave, which reads as
/// "alive" instead of a plain VU meter.
/// </summary>
public sealed class BarsView : FrameworkElement
{
    private const int Count = 9;
    private const double BarWidth = 3.0;
    private const double Gap = 4.0;
    private const double BarMinHeight = 4.0;

    private readonly double[] _heights = new double[Count];
    private double _smoothed;
    private double _phase;
    private Brush _brush = Brushes.White;

    public BarsMode Mode { get; set; } = BarsMode.Arming;

    /// <summary>Raw level in [0,1], written from any thread.</summary>
    public float TargetLevel;

    public Brush Brush
    {
        get => _brush;
        set
        {
            _brush = value;
            if (_brush.CanFreeze) _brush.Freeze();
            InvalidateVisual();
        }
    }

    public BarsView()
    {
        for (int i = 0; i < Count; i++) _heights[i] = BarMinHeight;
    }

    /// <summary>Advance the animation by <paramref name="dt"/> seconds and repaint.</summary>
    public void Tick(double dt)
    {
        dt = Math.Clamp(dt, 0, 0.05);
        _phase += dt;

        double target = Mode == BarsMode.Live ? Math.Clamp(TargetLevel, 0, 1) : 0;
        // Fast attack, slower release: the bars jump with a syllable and settle gently.
        double k = target > _smoothed ? 1 - Math.Exp(-dt * 40) : 1 - Math.Exp(-dt * 9);
        _smoothed += (target - _smoothed) * k;

        double maxHeight = ActualHeight <= 0 ? 26 : ActualHeight;
        double amp = Math.Pow(_smoothed, 0.7);
        int c = Count / 2;

        for (int i = 0; i < Count; i++)
        {
            double h;
            switch (Mode)
            {
                case BarsMode.Live:
                {
                    double boost = 1 - 0.5 * Math.Abs(i - c) / c;
                    double wave = 0.55 + 0.45 * Math.Sin(_phase * 9 + i * 0.6);
                    double idle = 0.6 * (0.5 + 0.5 * Math.Sin(_phase * 2.2 + i * 0.9)); // faint breathing at silence
                    h = BarMinHeight + idle + (maxHeight - BarMinHeight - idle) * amp * wave * boost;
                    break;
                }
                case BarsMode.Processing:
                {
                    // A bright pulse sweeps left to right and back, so "thinking" reads clearly.
                    double sweep = (_phase * 1.5) % 2.0;
                    double centerPos = (sweep <= 1 ? sweep : 2 - sweep) * (Count - 1);
                    double dist = i - centerPos;
                    double pulse = Math.Exp(-dist * dist * 0.8);
                    h = BarMinHeight + (maxHeight - BarMinHeight) * (0.18 + 0.82 * pulse);
                    break;
                }
                default:
                    h = BarMinHeight;
                    break;
            }
            // Ease each bar toward its target so mode changes glide instead of snapping.
            _heights[i] += (h - _heights[i]) * (1 - Math.Exp(-dt * 30));
        }
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Count * BarWidth + (Count - 1) * Gap, double.IsInfinity(availableSize.Height) ? 26 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double totalWidth = Count * BarWidth + (Count - 1) * Gap;
        double x = (ActualWidth - totalWidth) / 2;
        double cy = ActualHeight / 2;
        for (int i = 0; i < Count; i++)
        {
            double h = Math.Max(BarMinHeight, _heights[i]);
            var rect = new Rect(x, cy - h / 2, BarWidth, h);
            dc.DrawRoundedRectangle(_brush, null, rect, BarWidth / 2, BarWidth / 2);
            x += BarWidth + Gap;
        }
    }
}
