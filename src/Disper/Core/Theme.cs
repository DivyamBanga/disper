using System.Windows.Media;
using Microsoft.Win32;

namespace Disper.Core;

/// <summary>Accent palette and system theme lookups shared by the pill and the dashboard.</summary>
public static class Theme
{
    public static readonly (string Id, string Name, Color Color)[] Accents =
    {
        ("graphite", "Graphite", Color.FromRgb(0xE4, 0xE4, 0xE7)),
        ("blue", "Blue", Color.FromRgb(0x5B, 0x8D, 0xEF)),
        ("teal", "Teal", Color.FromRgb(0x2F, 0xB8, 0xA6)),
        ("green", "Green", Color.FromRgb(0x4C, 0xC3, 0x8A)),
        ("amber", "Amber", Color.FromRgb(0xF2, 0xA6, 0x2E)),
        ("coral", "Coral", Color.FromRgb(0xF0, 0x6A, 0x5C)),
        ("violet", "Violet", Color.FromRgb(0x8B, 0x7C, 0xF6)),
    };

    public static Color AccentColor(string id)
    {
        foreach (var a in Accents)
            if (a.Id == id) return a.Color;
        return Accents[1].Color;
    }

    /// <summary>Whether Windows is using the light taskbar/system theme (affects the tray glyph color).</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>Whether apps should use the light theme (the dashboard follows this).</summary>
    public static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return true; }
    }
}
