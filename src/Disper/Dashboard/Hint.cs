using System.Windows;

namespace Disper.Dashboard;

/// <summary>Attached placeholder text for the themed TextBox template.</summary>
public static class Hint
{
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Hint), new PropertyMetadata(""));

    public static string GetPlaceholder(DependencyObject o) => (string)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string value) => o.SetValue(PlaceholderProperty, value);
}
