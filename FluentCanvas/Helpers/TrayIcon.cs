using System;
using System.Drawing;
using System.IO;
using System.Windows.Interop;
using FluentCanvas.Localization;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FluentCanvas.Helpers;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint KeySelect = PInvoke.NIN_SELECT | 1; // NIN_KEYSELECT = NIN_SELECT | NINF_KEY
    private const uint IconId = 1;
    private const int OpenCommand = 1;
    private const int ExitCommand = 2;

    private readonly MainWindow owner;
    private readonly HwndSource source;
    private readonly HwndSourceHook hook;
    private readonly uint taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
    private bool registered;
    private bool menuOpen;
    private bool disposed;

    public TrayIcon(MainWindow owner)
    {
        this.owner = owner;
        hook = WindowMessage;
        // Explorer broadcasts TaskbarCreated to top-level windows.
        source = new HwndSource(new HwndSourceParameters("FluentCanvas notification area")
        {
            WindowStyle = unchecked((int)WINDOW_STYLE.WS_POPUP),
            ExtendedWindowStyle = (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
            HwndSourceHook = hook
        });
        try { Register(); }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private HWND Handle => (HWND)source.Handle;

    private unsafe NOTIFYICONDATAW IconData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = Handle,
        uID = IconId
    };

    private void Register()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "FluentCanvas.ico");
        var result = PInvoke.LoadIconMetric(null, path, _LI_METRIC.LIM_SMALL, out var icon);
        using var loadedIcon = icon;
        if (result.Failed || loadedIcon.IsInvalid)
        {
            LogHelper.WriteLogToFile("Could not load the notification area icon.", LogHelper.LogType.Error);
            return;
        }

        var data = IconData();
        data.hIcon = (HICON)loadedIcon.DangerousGetHandle();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE |
            NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        "FluentCanvas".AsSpan().CopyTo(data.szTip.AsSpan());
        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;

        registered = (PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, data) ||
                      PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data)) &&
                     PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, data);
        if (!registered)
        {
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, data);
            LogHelper.WriteLogToFile("Could not register the notification area icon.", LogHelper.LogType.Error);
        }
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (disposed || source == null) return 0;
        if (taskbarCreated != 0 && message == taskbarCreated)
        {
            Register();
            return 0;
        }
        if (!registered || message != CallbackMessage || ((uint)lParam >> 16) != IconId)
            return 0;

        switch ((uint)lParam & 0xffff)
        {
            case PInvoke.NIN_SELECT:
            case KeySelect:
                owner.ActivateCanvas();
                break;
            case PInvoke.WM_CONTEXTMENU:
                ShowMenu();
                break;
            default:
                return 0;
        }

        handled = true;
        return 0;
    }

    private unsafe void ShowMenu()
    {
        if (menuOpen) return;

        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER),
            hWnd = Handle,
            uID = IconId
        };
        Point anchor;
        if (PInvoke.Shell_NotifyIconGetRect(identifier, out var bounds).Succeeded)
            anchor = new Point(bounds.left, bounds.top);
        else if (!PInvoke.GetCursorPos(out anchor))
            return;

        using var menu = PInvoke.CreatePopupMenu_SafeHandle();
        if (menu.IsInvalid) return;

        menuOpen = true;
        int command = 0;
        try
        {
            if (!PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, OpenCommand, UiText.Get("Canvas.WindowTitle")) ||
                !PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, ExitCommand, UiText.Get("Settings.ExitApp")))
                return;

            var window = Handle;
            PInvoke.SetForegroundWindow(window);
            command = PInvoke.TrackPopupMenu(menu, TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD |
                TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON |
                TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN, anchor.X, anchor.Y, window, null).Value;
            // Post a message so subsequent notification area menus dismiss correctly.
            if (!disposed) PInvoke.PostMessage(window, PInvoke.WM_NULL, default, default);
        }
        finally
        {
            menuOpen = false;
        }

        if (disposed) return;
        switch (command)
        {
            case OpenCommand:
                owner.ActivateCanvas();
                break;
            case ExitCommand:
                owner.ExitFromTray();
                break;
            default:
                var data = IconData();
                PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETFOCUS, data);
                break;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (registered)
        {
            var data = IconData();
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, data);
            registered = false;
        }
        source.Dispose();
    }
}
