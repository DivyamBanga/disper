using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Disper.Core;

namespace Disper.Tray;

/// <summary>
/// Notification-area icon over Shell_NotifyIcon with a hidden message window. Left click opens the
/// dashboard, right click shows a WPF context menu (styled by the app theme). Survives Explorer restarts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Native.WM_USER + 2;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private readonly string _assetsDir;
    private nint _icon;
    private bool _added;
    private bool _active;
    private string _tip = "Disper";

    public event Action? OpenRequested;
    public Func<ContextMenu>? MenuFactory { get; set; }

    public TrayIcon()
    {
        _assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        var p = new HwndSourceParameters("DisperTray")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = 0,
            ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        Add();
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        Update();
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        Update();
    }

    private nint LoadIcon()
    {
        var file = _active ? "tray-active.ico" : Theme.SystemUsesLightTheme() ? "tray-light.ico" : "tray-dark.ico";
        uint dpi = Native.GetDpiForWindow(_source.Handle);
        int size = (int)Math.Round(16 * (dpi == 0 ? 96 : dpi) / 96.0);
        var h = Native.LoadImageW(0, Path.Combine(_assetsDir, file), Native.IMAGE_ICON, size, size, Native.LR_LOADFROMFILE);
        if (h == 0) Log.Warn($"tray icon load failed: {file}");
        return h;
    }

    private Native.NOTIFYICONDATAW Data()
    {
        return new Native.NOTIFYICONDATAW
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tip,
            szInfo = "",
            szInfoTitle = "",
        };
    }

    private void Add()
    {
        SwapIcon();
        var d = Data();
        _added = Native.Shell_NotifyIconW(Native.NIM_ADD, ref d);
    }

    private void Update()
    {
        if (!_added)
        {
            Add();
            return;
        }
        SwapIcon();
        var d = Data();
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref d);
    }

    private void SwapIcon()
    {
        var old = _icon;
        _icon = LoadIcon();
        if (old != 0) Native.DestroyIcon(old);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case Native.WM_LBUTTONUP:
                case Native.WM_LBUTTONDBLCLK:
                    OpenRequested?.Invoke();
                    break;
                case Native.WM_RBUTTONUP:
                case Native.WM_CONTEXTMENU:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated)
        {
            _added = false;
            Add();
        }
        return 0;
    }

    private void ShowMenu()
    {
        var menu = MenuFactory?.Invoke();
        if (menu is null) return;
        // The menu must belong to the foreground window, otherwise it does not close on an outside click.
        Native.SetForegroundWindow(_source.Handle);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data();
            Native.Shell_NotifyIconW(Native.NIM_DELETE, ref d);
            _added = false;
        }
        if (_icon != 0) Native.DestroyIcon(_icon);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
