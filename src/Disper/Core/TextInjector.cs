using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Disper.Core;

public enum InsertOutcome { Inserted, Typed, CopiedOnly, Failed }

/// <summary>
/// Puts text into whatever has keyboard focus. Paste mode swaps the clipboard, sends the paste chord and
/// restores the previous clipboard once the target has had time to read it. Type mode sends Unicode key
/// events and never touches the clipboard. Both stamp their input so the hotkey hook ignores it.
/// </summary>
public static class TextInjector
{
    private static readonly int[] Modifiers =
    {
        Native.VK_LSHIFT, Native.VK_RSHIFT, Native.VK_LCONTROL, Native.VK_RCONTROL,
        Native.VK_LMENU, Native.VK_RMENU, Native.VK_LWIN, Native.VK_RWIN,
    };

    private static readonly uint CfHtml = Native.RegisterClipboardFormatW("HTML Format");
    private static readonly uint CfRtf = Native.RegisterClipboardFormatW("Rich Text Format");
    private static readonly uint CfPng = Native.RegisterClipboardFormatW("PNG");
    private static readonly uint CfExclude = Native.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CfNoHistory = Native.RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private static readonly uint CfNoCloud = Native.RegisterClipboardFormatW("CanUploadToCloudClipboard");

    private static readonly HashSet<string> ConsoleClasses = new(StringComparer.Ordinal)
    {
        "ConsoleWindowClass", "mintty", "PuTTY", "VirtualConsoleClass",
    };

    private const string WindowsTerminalClass = "CASCADIA_HOSTING_WINDOW_CLASS";

    /// <summary>Restore the user's clipboard this long after pasting. Electron apps read it late.</summary>
    private const int RestoreDelayMs = 350;

    public static InsertOutcome Insert(string text, InsertionMode mode, nint ownProcessWindowCheck = 0)
    {
        if (string.IsNullOrEmpty(text)) return InsertOutcome.Failed;

        var target = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(target, out var pid);
        if (target == 0 || pid == Environment.ProcessId)
        {
            // Nothing sensible to paste into (or it is our own dashboard): leave the text on the clipboard.
            SetClipboardText(text, excludeFromHistory: false);
            return InsertOutcome.CopiedOnly;
        }

        return mode == InsertionMode.Type ? TypeText(text) : PasteText(text, target);
    }

    // ---------- paste ----------

    private static InsertOutcome PasteText(string text, nint target)
    {
        var snapshot = SnapshotClipboard();
        if (!SetClipboardText(text, excludeFromHistory: true))
        {
            Log.Warn("clipboard busy; falling back to typing");
            return TypeText(text);
        }
        uint ourSequence = Native.GetClipboardSequenceNumber();

        var cls = Native.GetClassName(target);
        var held = ReleaseHeldModifiers();
        var chord = new List<Native.INPUT>();
        if (cls == WindowsTerminalClass)
        {
            chord.Add(Native.KeyInput(Native.VK_CONTROL, false));
            chord.Add(Native.KeyInput(Native.VK_SHIFT, false));
            chord.Add(Native.KeyInput(Native.VK_V, false));
            chord.Add(Native.KeyInput(Native.VK_V, true));
            chord.Add(Native.KeyInput(Native.VK_SHIFT, true));
            chord.Add(Native.KeyInput(Native.VK_CONTROL, true));
        }
        else if (ConsoleClasses.Contains(cls))
        {
            chord.Add(Native.KeyInput(Native.VK_SHIFT, false));
            chord.Add(Native.KeyInput(Native.VK_INSERT, false, extended: true));
            chord.Add(Native.KeyInput(Native.VK_INSERT, true, extended: true));
            chord.Add(Native.KeyInput(Native.VK_SHIFT, true));
        }
        else
        {
            chord.Add(Native.KeyInput(Native.VK_CONTROL, false));
            chord.Add(Native.KeyInput(Native.VK_V, false));
            chord.Add(Native.KeyInput(Native.VK_V, true));
            chord.Add(Native.KeyInput(Native.VK_CONTROL, true));
        }
        var sent = Native.Send(chord.ToArray());
        RepressModifiers(held);

        if (sent != chord.Count)
        {
            Log.Warn($"SendInput sent {sent}/{chord.Count} (foreground may be elevated)");
            RestoreClipboard(snapshot, ourSequence);
            return InsertOutcome.Failed;
        }

        // Restore later, and only if nobody else has written to the clipboard in the meantime.
        Task.Delay(RestoreDelayMs).ContinueWith(_ => RestoreClipboard(snapshot, ourSequence));
        return InsertOutcome.Inserted;
    }

    private static List<int> ReleaseHeldModifiers()
    {
        var held = new List<int>();
        foreach (var vk in Modifiers)
            if (Native.IsKeyDown(vk)) held.Add(vk);
        if (held.Count > 0)
        {
            Native.Send(held.Select(vk => Native.KeyInput((ushort)vk, true, IsExtended(vk))).ToArray());
            Thread.Sleep(5);
        }
        return held;
    }

    private static void RepressModifiers(List<int> held)
    {
        if (held.Count == 0) return;
        // Press them again so the user's physical release later leaves the OS state consistent.
        Native.Send(held.Select(vk => Native.KeyInput((ushort)vk, false, IsExtended(vk))).ToArray());
    }

    private static bool IsExtended(int vk) =>
        vk is Native.VK_RCONTROL or Native.VK_RMENU or Native.VK_LWIN or Native.VK_RWIN or Native.VK_INSERT;

    // ---------- type ----------

    private static InsertOutcome TypeText(string text)
    {
        var inputs = new List<Native.INPUT>(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                AddKey(inputs, Native.VK_RETURN);
            }
            else if (c == '\n') AddKey(inputs, Native.VK_RETURN);
            else if (c == '\t') AddKey(inputs, Native.VK_TAB);
            else
            {
                inputs.Add(Native.UnicodeInput(c, false));
                inputs.Add(Native.UnicodeInput(c, true));
            }
        }

        var held = ReleaseHeldModifiers();
        const int chunk = 64;
        uint sentTotal = 0;
        for (int i = 0; i < inputs.Count; i += chunk)
        {
            var slice = inputs.Skip(i).Take(chunk).ToArray();
            sentTotal += Native.Send(slice);
        }
        RepressModifiers(held);
        return sentTotal == inputs.Count ? InsertOutcome.Typed : InsertOutcome.Failed;
    }

    private static void AddKey(List<Native.INPUT> inputs, int vk)
    {
        inputs.Add(Native.KeyInput((ushort)vk, false));
        inputs.Add(Native.KeyInput((ushort)vk, true));
    }

    // ---------- clipboard ----------

    private sealed record ClipItem(uint Format, byte[] Data);

    private static bool OpenClipboardRetry()
    {
        for (int i = 0; i < 10; i++)
        {
            if (Native.OpenClipboard(0)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>Copies the formats we know how to put back. Private/delayed-render formats are not preserved.</summary>
    private static List<ClipItem> SnapshotClipboard()
    {
        var items = new List<ClipItem>();
        if (!OpenClipboardRetry()) return items;
        try
        {
            uint fmt = 0;
            while ((fmt = Native.EnumClipboardFormats(fmt)) != 0)
            {
                if (fmt is not (Native.CF_UNICODETEXT or Native.CF_HDROP or Native.CF_DIB or Native.CF_DIBV5) &&
                    fmt != CfHtml && fmt != CfRtf && fmt != CfPng) continue;
                var h = Native.GetClipboardData(fmt);
                if (h == 0) continue;
                var size = (int)Native.GlobalSize(h);
                var p = Native.GlobalLock(h);
                if (p == 0) continue;
                try
                {
                    var data = new byte[size];
                    Marshal.Copy(p, data, 0, size);
                    items.Add(new ClipItem(fmt, data));
                }
                finally { Native.GlobalUnlock(h); }
            }
        }
        finally { Native.CloseClipboard(); }
        return items;
    }

    private static bool SetClipboardText(string text, bool excludeFromHistory)
    {
        if (!OpenClipboardRetry()) return false;
        try
        {
            Native.EmptyClipboard();
            PutBytes(Native.CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
            if (excludeFromHistory)
            {
                var zero = new byte[4];
                PutBytes(CfExclude, zero);
                PutBytes(CfNoHistory, zero);
                PutBytes(CfNoCloud, zero);
            }
            return true;
        }
        finally { Native.CloseClipboard(); }
    }

    private static void RestoreClipboard(List<ClipItem> snapshot, uint ourSequence)
    {
        try
        {
            if (Native.GetClipboardSequenceNumber() != ourSequence) return; // someone copied since; leave it
            if (!OpenClipboardRetry()) return;
            try
            {
                Native.EmptyClipboard();
                foreach (var item in snapshot) PutBytes(item.Format, item.Data);
            }
            finally { Native.CloseClipboard(); }
        }
        catch (Exception ex)
        {
            Log.Error("clipboard restore failed", ex);
        }
    }

    private static void PutBytes(uint format, byte[] data)
    {
        var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (nuint)data.Length);
        if (h == 0) return;
        var p = Native.GlobalLock(h);
        if (p == 0)
        {
            Native.GlobalFree(h);
            return;
        }
        Marshal.Copy(data, 0, p, data.Length);
        Native.GlobalUnlock(h);
        if (Native.SetClipboardData(format, h) == 0) Native.GlobalFree(h); // the system owns it on success
    }

    /// <summary>Process name of the foreground window, for the history list.</summary>
    public static string ForegroundAppName()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }
}
