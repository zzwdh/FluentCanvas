using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace FluentCanvas;

public partial class MainWindow
{
    private enum PageDirection { Previous, Next }

    private const int WmAppCommand = 0x0319;
    private const int AppCommandIdMask = 0x0FFF;
    private const int AppCommandBrowserBackward = 1;
    private const int AppCommandBrowserForward = 2;
    private HwndSourceHook pageNavigationHook;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (source == null) return;

        // Keep the delegate alive for the lifetime of this window's source.
        pageNavigationHook = PageNavigationWindowMessage;
        source.AddHook(pageNavigationHook);
    }

    private nint PageNavigationWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmAppCommand || !IsActive) return 0;

        // GET_APPCOMMAND_LPARAM: take the high word without the device flags.
        int command = (int)(lParam >> 16) & AppCommandIdMask;
        PageDirection? direction = command switch
        {
            AppCommandBrowserBackward => PageDirection.Previous,
            AppCommandBrowserForward => PageDirection.Next,
            _ => null
        };
        if (direction is not { } pageDirection || !TryNavigatePage(pageDirection, Keyboard.FocusedElement)) return 0;

        handled = true;
        return 1;
    }

    private bool HandlePageNavigationKey(KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return false;

        PageDirection? direction = e.Key switch
        {
            Key.PageUp or Key.BrowserBack => PageDirection.Previous,
            Key.PageDown or Key.BrowserForward => PageDirection.Next,
            _ => null
        };
        return direction is { } pageDirection && TryNavigatePage(pageDirection, e.OriginalSource, e.IsRepeat);
    }

    private bool TryNavigatePage(PageDirection direction, object inputSource, bool isRepeat = false)
    {
        if (!IsEnabled || (currentMode == 0 && BtnPPTSlideShowEnd.Visibility != Visibility.Visible) ||
            inputSource is TextBoxBase or PasswordBox or Selector or RangeBase ||
            inkCanvas.IsMouseCaptureWithin || inkCanvas.IsStylusCaptureWithin || inkCanvas.AreAnyTouchesCapturedWithin)
            return false;

        if (isRepeat) return true;

        if (currentMode == 0)
        {
            if (direction == PageDirection.Previous) BtnPPTSlidesUp_Click(null, null);
            else BtnPPTSlidesDown_Click(null, null);
        }
        else if (direction == PageDirection.Previous && CurrentWhiteboardIndex > 1)
        {
            ShowPreviousWhiteboard();
        }
        else if (direction == PageDirection.Next && CurrentWhiteboardIndex < WhiteboardTotalCount)
        {
            ShowNextWhiteboard();
        }

        return true;
    }
}
