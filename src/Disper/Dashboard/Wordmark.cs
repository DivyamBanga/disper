using System.Windows;
using System.Windows.Media;
using Disper.Core;

namespace Disper.Dashboard;

/// <summary>The four-bar Disper mark, drawn in the accent color so it matches the pill.</summary>
public sealed class Wordmark : FrameworkElement
{
    private static readonly double[] Bars = { 0.42, 0.78, 1.0, 0.58 };

    protected override void OnRender(DrawingContext dc)
    {
        var color = Theme.AccentColor(App.Settings.Current.Accent);
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        double w = ActualWidth, h = ActualHeight;
        double bw = w * 0.16, gap = w * 0.12;
        double total = Bars.Length * bw + (Bars.Length - 1) * gap;
        double x = (w - total) / 2;
        double cy = h / 2;
        foreach (var f in Bars)
        {
            double bh = h * f;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, cy - bh / 2, bw, bh), bw / 2, bw / 2);
            x += bw + gap;
        }
    }
}
