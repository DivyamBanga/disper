using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Disper.Core;
using Disper.Dashboard;
using Disper.Overlay;
using Disper.Themes;
using Disper.Tray;
using Microsoft.Win32;

namespace Disper;

public partial class App : Application
{
    private const string MutexName = @"Local\Disper.SingleInstance";
    private const string ShowEventName = @"Local\Disper.ShowDashboard";

    public static SettingsStore Settings { get; } = new();
    public static HistoryStore History { get; } = new();
    public static Transcriber Transcriber { get; } = new();
    public static AudioCapture Audio { get; } = new();
    public static HotkeyService Hotkey { get; } = new();
    public static SoundCues? Sounds { get; private set; }
    public static DictationController? Controller { get; private set; }
    public static App Instance => (App)Current;

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private TrayIcon? _tray;
    private OverlayWindow? _overlay;
    private DashboardWindow? _dashboard;

    /// <summary>Progress of the first-run model download, for the dashboard.</summary>
    public event Action<DownloadProgress?>? SetupProgress;
    public DownloadProgress? CurrentSetup { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out var isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirst)
        {
            _showEvent.Set(); // ask the running instance to open its dashboard, then leave
            Shutdown();
            return;
        }
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowDashboard), null, -1, false);

        AppPaths.EnsureDirectories();
        Log.Info("Disper starting");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled", args.Exception);
            args.Handled = true;
        };

        bool firstRun = !File.Exists(AppPaths.SettingsFile);
        Settings.Load();
        History.Load();
        if (firstRun) Settings.Save();

        ThemeManager.Apply(Settings.Current.Accent);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Sounds = new SoundCues { Enabled = Settings.Current.SoundCues };
        Controller = new DictationController(Settings, History, Transcriber, Audio, Hotkey, Sounds, Dispatcher);
        Controller.StateChanged += (state, _) => _tray?.SetActive(state is SessionState.Arming or SessionState.Listening or SessionState.HandsFree);

        _overlay = new OverlayWindow(Controller);
        _overlay.Show();       // stays visible but fully transparent; the pill fades in on demand

        _tray = new TrayIcon { MenuFactory = BuildTrayMenu };
        _tray.OpenRequested += ShowDashboard;

        Hotkey.Start(HotkeyService.VkFor(Settings.Current.Hotkey));
        Settings.Changed += OnSettingsChanged;
        Autostart.Set(Settings.Current.StartAtLogin);

        Task.Run(() => Audio.Prepare(Settings.Current.MicrophoneId));
        _ = LoadModelAsync();

        bool background = e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        if (!background || firstRun) ShowDashboard();
    }

    private async Task LoadModelAsync()
    {
        var model = ModelCatalog.Get(Settings.Current.ModelId);
        try
        {
            if (!model.IsInstalled)
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    CurrentSetup = p;
                    Controller!.SetupMessage = "Downloading speech model…";
                    SetupProgress?.Invoke(p);
                });
                await ModelManager.EnsureInstalledAsync(model, progress, CancellationToken.None);
            }
            CurrentSetup = new DownloadProgress(1, "Loading model…");
            SetupProgress?.Invoke(CurrentSetup);
            Controller!.SetupMessage = null;
            await Transcriber.LoadAsync(model, Settings.Current.Threads);
        }
        catch (Exception ex)
        {
            Log.Error("model setup failed", ex);
            CurrentSetup = new DownloadProgress(0, "Model download failed: " + ex.Message);
            Controller!.SetupMessage = "Model download failed";
        }
        finally
        {
            if (Transcriber.IsReady)
            {
                CurrentSetup = null;
                Controller!.SetupMessage = null;
            }
            SetupProgress?.Invoke(CurrentSetup);
        }
    }

    private void OnSettingsChanged(Settings s)
    {
        Hotkey.SetKey(HotkeyService.VkFor(s.Hotkey));
        Sounds!.Enabled = s.SoundCues;
        Autostart.Set(s.StartAtLogin);
        ThemeManager.ApplyAccent(s.Accent);
        _overlay?.ApplyAccent();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(() =>
            {
                ThemeManager.ApplyPalette(!Theme.AppsUseLightTheme());
                ThemeManager.ApplyAccent(Settings.Current.Accent);
                _tray?.SetActive(Controller?.State is SessionState.Arming or SessionState.Listening or SessionState.HandsFree);
            });
    }

    /// <summary>Re-open the microphone (after the user picks a different one).</summary>
    public void ReloadMicrophone() => Task.Run(() => Audio.Prepare(Settings.Current.MicrophoneId));

    /// <summary>Swap the recognizer (after the user picks a different model or thread count).</summary>
    public Task ReloadModelAsync() => LoadModelAsync();

    public void ShowDashboard()
    {
        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow();
            _dashboard.Closed += (_, _) => _dashboard = null;
        }
        _dashboard.Show();
        if (_dashboard.WindowState == WindowState.Minimized) _dashboard.WindowState = WindowState.Normal;
        _dashboard.Activate();
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Open Disper", Command = new Relay(ShowDashboard) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Sound cues",
            IsCheckable = true,
            IsChecked = Settings.Current.SoundCues,
            Command = new Relay(() => Settings.Update(s => s.SoundCues = !s.SoundCues)),
        });
        menu.Items.Add(new MenuItem
        {
            Header = "Paste instantly",
            IsCheckable = true,
            IsChecked = Settings.Current.InsertionMode == InsertionMode.Paste,
            Command = new Relay(() => Settings.Update(s =>
                s.InsertionMode = s.InsertionMode == InsertionMode.Paste ? InsertionMode.Type : InsertionMode.Paste)),
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Quit Disper", Command = new Relay(Quit) });
        return menu;
    }

    public void Quit()
    {
        Log.Info("Disper quitting");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _showWait?.Unregister(null);
        _tray?.Dispose();
        Controller?.Dispose();
        Hotkey.Dispose();
        Audio.Dispose();
        Transcriber.Dispose();
        Sounds?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Minimal ICommand so menu items can call plain methods.</summary>
public sealed class Relay : System.Windows.Input.ICommand
{
    private readonly Action _action;
    public Relay(Action action) => _action = action;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
