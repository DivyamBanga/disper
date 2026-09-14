using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Disper.Core;

/// <summary>
/// Global push-to-talk key via a WH_KEYBOARD_LL hook. The hook lives on its own thread with its own message
/// loop and the callback does almost nothing, because Windows silently removes a hook whose callback stalls
/// past LowLevelHooksTimeout. The chosen key is swallowed entirely so other apps never see it (a lone Alt
/// would otherwise focus menu bars). Events fire on the hook thread; handlers must return immediately.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const uint MsgReinstall = Native.WM_USER + 1;

    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Native.LowLevelKeyboardProc? _proc; // kept alive for the lifetime of the hook
    private volatile int _vk;
    private bool _down;
    private long _downAt;
    private volatile bool _captureEscape;
    private int _otherKeyCounter;
    private bool _disposed;

    /// <summary>Hotkey pressed (first down only; auto-repeats are ignored).</summary>
    public event Action? HotkeyDown;

    /// <summary>Hotkey released, with how long it was held.</summary>
    public event Action<TimeSpan>? HotkeyUp;

    /// <summary>Escape pressed while <see cref="CaptureEscape"/> was on. The key is swallowed.</summary>
    public event Action? EscapePressed;

    /// <summary>Set while a dictation is in progress so Esc cancels it instead of reaching the app.</summary>
    public bool CaptureEscape
    {
        set => _captureEscape = value;
    }

    /// <summary>Bumps whenever any other key goes down. Lets the app tell "user typed since last insert".</summary>
    public int OtherKeyCounter => Volatile.Read(ref _otherKeyCounter);

    public void Start(int vk)
    {
        _vk = vk;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "Disper.Hook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public void SetKey(int vk)
    {
        _vk = vk;
        _down = false;
    }

    /// <summary>Re-install the hook. Cheap insurance after sleep, lock screens and UAC prompts, where key-ups get lost.</summary>
    public void Reinstall()
    {
        if (_threadId != 0) Native.PostThreadMessageW(_threadId, MsgReinstall, 0, 0);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Reinstall();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.SessionLogon)
            Reinstall();
    }

    private void HookThread()
    {
        _threadId = Native.GetCurrentThreadId();
        _proc = Callback;
        Install();

        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == MsgReinstall)
            {
                Uninstall();
                _down = false;
                Install();
                continue;
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        Uninstall();
    }

    private void Install()
    {
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc!, Native.GetModuleHandleW(null), 0);
        if (_hook == 0) Log.Error($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        else Log.Info("keyboard hook installed");
    }

    private void Uninstall()
    {
        if (_hook != 0)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    private nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        if ((k.flags & Native.LLKHF_INJECTED) != 0 && !(TestMode.Enabled && k.dwExtraInfo == Native.TestMarker))
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam); // ours or another tool's; never a hotkey

        bool up = (k.flags & Native.LLKHF_UP) != 0;
        int vk = (int)k.vkCode;

        if (vk == _vk)
        {
            if (!up)
            {
                if (!_down)
                {
                    _down = true;
                    _downAt = Stopwatch.GetTimestamp();
                    HotkeyDown?.Invoke();
                }
            }
            else if (_down)
            {
                _down = false;
                HotkeyUp?.Invoke(Stopwatch.GetElapsedTime(_downAt));
            }
            return 1; // swallow
        }

        if (!up)
        {
            if (vk == Native.VK_ESCAPE && _captureEscape)
            {
                EscapePressed?.Invoke();
                return 1;
            }
            Interlocked.Increment(ref _otherKeyCounter);
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_threadId != 0) Native.PostThreadMessageW(_threadId, Native.WM_QUIT, 0, 0);
        _thread?.Join(500);
    }

    /// <summary>Keys offered in Settings. Anything that is never used as a modifier for real shortcuts is a candidate.</summary>
    public static readonly (string Id, string Label, int Vk)[] Choices =
    {
        ("RightAlt", "Right Alt", Native.VK_RMENU),
        ("RightCtrl", "Right Ctrl", Native.VK_RCONTROL),
        ("RightShift", "Right Shift", Native.VK_RSHIFT),
        ("CapsLock", "Caps Lock", 0x14),
        ("ScrollLock", "Scroll Lock", 0x91),
        ("Pause", "Pause", 0x13),
        ("Insert", "Insert", Native.VK_INSERT),
        ("Menu", "Menu key", 0x5D),
        ("F13", "F13", 0x7C),
        ("F14", "F14", 0x7D),
    };

    public static int VkFor(string id) => Choices.FirstOrDefault(c => c.Id == id).Vk is var vk && vk != 0 ? vk : Native.VK_RMENU;

    public static string LabelFor(string id) => Choices.FirstOrDefault(c => c.Id == id).Label ?? "Right Alt";
}
