using FluentCanvas.Localization;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace FluentCanvas;

internal sealed class Hotkey : IDisposable
{
    private const int HotkeyAlreadyRegistered = 1409;
    private readonly HwndSource source;
    private readonly Dictionary<int, Action> callbacks = new();
    private int nextId;

    internal Hotkey(Window window)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        source.AddHook(WndProc);
    }

    internal (int Id, string Error) Register(KeyGesture gesture, Action callback)
    {
        int id = nextId;
        // Window-owned identifiers passed to RegisterHotKey must stay in 0x0000–0xBFFF.
        do
        {
            id = (id + 1) % 0xC000;
            if (!callbacks.ContainsKey(id)) break;
        }
        while (id != nextId);
        if (callbacks.ContainsKey(id)) return (-1, UiText.Get("Settings.HotkeyLimit"));
        var modifiers = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HOT_KEY_MODIFIERS.MOD_ALT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HOT_KEY_MODIFIERS.MOD_WIN;
        var key = KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (key == 0) return (-1, UiText.Get("Settings.HotkeyUnknownKey"));
        if (!PInvoke.RegisterHotKey((HWND)source.Handle, id, modifiers, (uint)key))
        {
            int error = Marshal.GetLastPInvokeError();
            return (-1, error == HotkeyAlreadyRegistered ? UiText.Get("Settings.HotkeyConflict") : UiText.Format("Settings.HotkeyWindowsError", error));
        }
        nextId = id;
        callbacks.Add(id, callback);
        return (id, null);
    }

    internal void Unregister(int id)
    {
        if (callbacks.Remove(id)) PInvoke.UnregisterHotKey((HWND)source.Handle, id);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == PInvoke.WM_HOTKEY && callbacks.TryGetValue((int)wParam, out var callback))
        {
            handled = true;
            callback();
        }
        return 0;
    }

    public void Dispose()
    {
        nint window = source.Handle;
        if (window != 0)
        {
            foreach (int id in callbacks.Keys) PInvoke.UnregisterHotKey((HWND)window, id);
        }
        callbacks.Clear();
        if (!source.IsDisposed) source.RemoveHook(WndProc);
    }
}
