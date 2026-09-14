using System.Windows.Markup;
using System.Windows;
using System.Windows.Controls;

namespace Disper.Dashboard;

/// <summary>Shared page scaffold: a title, an optional subtitle, an actions slot and a body below.</summary>
[ContentProperty(nameof(Body))]
public sealed class PageScaffold : Control
{
    static PageScaffold()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PageScaffold), new FrameworkPropertyMetadata(typeof(PageScaffold)));
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageScaffold));
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageScaffold));
    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageScaffold));
    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(nameof(Body), typeof(object), typeof(PageScaffold));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
}
