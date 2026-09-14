using System.Windows;
using System.Windows.Media;
using Disper.Core;

namespace Disper.Themes;

/// <summary>
/// Applies the light/dark palette (following Windows) and the accent color into the app's resources.
/// Colors are swapped in place so every brush that binds them updates live, no restart needed.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _palette;

    public static bool IsDark { get; private set; }

    public static void Apply(string accentId)
    {
        ApplyPalette(!Theme.AppsUseLightTheme());
        ApplyAccent(accentId);
    }

    public static void ApplyPalette(bool dark)
    {
        IsDark = dark;
        var source = new Uri($"Themes/Palette.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        var next = (ResourceDictionary)Application.LoadComponent(source);
        // Copy the colors into the app's root dictionary so DynamicResource brushes re-evaluate.
        foreach (var key in next.Keys)
            Application.Current.Resources[key] = next[key];
        _palette = next;
    }

    public static void ApplyAccent(string accentId)
    {
        var accent = Theme.AccentColor(accentId);
        var res = Application.Current.Resources;
        res["C.Accent"] = accent;
        res["C.AccentText"] = Readable(accent);
        res["C.AccentSoft"] = Soft(accent, IsDark);
    }

    /// <summary>Black or white text, whichever reads better on the accent (per WCAG relative luminance).</summary>
    private static Color Readable(Color c)
    {
        double L = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        return L > 0.4 ? Color.FromRgb(0x14, 0x14, 0x16) : Colors.White;
    }

    private static Color Soft(Color c, bool dark)
    {
        return dark
            ? Blend(c, Color.FromRgb(0x14, 0x14, 0x16), 0.82)
            : Blend(c, Colors.White, 0.86);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R * (1 - t) + b.R * t),
        (byte)(a.G * (1 - t) + b.G * t),
        (byte)(a.B * (1 - t) + b.B * t));

    private static double Lin(byte v)
    {
        double s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
