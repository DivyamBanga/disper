using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Disper.Dashboard;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmLabel, bool danger = true)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmBtn.Content = confirmLabel;
        if (danger) ConfirmBtn.Background = (Brush)FindResource("B.Danger");
        Loaded += (_, _) => CancelBtn.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; }
        base.OnKeyDown(e);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
