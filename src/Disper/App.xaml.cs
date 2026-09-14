using System.Windows;
using Disper.Core;

namespace Disper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureDirectories();
        Log.Info("Disper starting");
    }
}
