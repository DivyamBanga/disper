using System.Windows.Markup;
using System.Windows;
using System.Windows.Controls;

namespace Disper.Dashboard;

/// <summary>A labelled settings row: title and description on the left, a control on the right.</summary>
[ContentProperty(nameof(Control))]
public sealed class SettingRow : Control
{
    static SettingRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingRow), new FrameworkPropertyMetadata(typeof(SettingRow)));
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty ControlProperty =
        DependencyProperty.Register(nameof(Control), typeof(object), typeof(SettingRow));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object? Control { get => GetValue(ControlProperty); set => SetValue(ControlProperty, value); }
}
