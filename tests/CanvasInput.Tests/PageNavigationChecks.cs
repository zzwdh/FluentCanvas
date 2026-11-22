using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace FluentCanvas;

internal static class PageNavigationChecks
{
    internal static void Run()
    {
        VerifyApplicationCommands();

        var window = new MainWindow();
        try
        {
            window.BindPageNavigationForTest();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
            var canvas = window.Canvas;

            bool Press(Key key, bool repeat = false, UIElement origin = null)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                if (repeat)
                {
                    // WPF normally sets this value when the keyboard provider receives another key-down.
                    typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(args, [true]);
                }
                (origin ?? canvas).RaiseEvent(args);
                return args.Handled;
            }

            Check(!Press(Key.PageDown) && !Press(Key.BrowserBack),
                "Page inputs should pass through on the desktop without an active slide show.");

            window.SetPageNavigationContext(whiteboard: true, presentation: true);
            Check(Press(Key.PageUp) && window.BoardPage == 1,
                "Page Up should stop at the first whiteboard page.");
            Check(Press(Key.PageDown) && window.BoardPage == 2,
                "Page Down should advance the whiteboard even when a slide show is active.");
            Check(Press(Key.PageDown, repeat: true) && window.BoardPage == 2,
                "A held button should consume repeats without changing more pages.");
            Check(Press(Key.BrowserForward) && window.BoardPage == 3,
                "A browser forward key should advance the whiteboard.");
            Check(Press(Key.PageDown) && window.BoardPage == 3 && window.BoardPageCount == 3,
                "Page Down at the last page should not create another page.");
            Check(Press(Key.BrowserBack) && window.BoardPage == 2,
                "A browser back key should return to the previous whiteboard page.");
            Check(!Press(Key.A) && !Press(Key.Back) && window.BoardPage == 2,
                "Unrelated keys should pass through without changing pages.");
            Check(window.PresentationPageChanges == 0,
                "Whiteboard navigation must leave the active slide show alone.");

            var editor = new TextBox();
            canvas.Children.Add(editor);
            Check(!Press(Key.PageUp, origin: editor) && window.BoardPage == 2,
                "Page keys should pass through to the text editor receiving them.");
            window.IsEnabled = false;
            Check(!Press(Key.BrowserBack) && window.BoardPage == 2,
                "A disabled window should leave its pages unchanged.");
            window.IsEnabled = true;

            window.SetPageNavigationContext(whiteboard: false, presentation: true);
            Check(Press(Key.PageDown) && window.PresentationPageOffset == 1,
                "Page Down should advance an active slide show in desktop mode.");
            Check(Press(Key.PageUp) && window.PresentationPageOffset == 0,
                "Page Up should return to the previous slide in desktop mode.");
            Check(Press(Key.BrowserForward) && window.PresentationPageOffset == 1,
                "A browser forward key should advance the active slide show.");
            Check(Press(Key.BrowserBack) && window.PresentationPageOffset == 0,
                "A browser back key should return to the previous slide.");
        }
        finally
        {
            window.Close();
        }
        Console.WriteLine("PASS Page keys and browser commands route to the active whiteboard or slide show with page and repeat limits");
    }

    private static void VerifyApplicationCommands()
    {
        const int AppCommand = 0x0319, XButtonUp = 0x020C;
        const int BrowserBackward = 1, BrowserForward = 2, BrowserRefresh = 3, MouseDevice = 0x8000;

        var window = new MainWindow();
        try
        {
            var interop = new WindowInteropHelper(window);
            window.SourceInitialized += (_, _) =>
            {
                // WPF runs the most recently added hook first. Keep unhandled test messages
                // from reaching the shell by registering before the production hook.
                HwndSource.FromHwnd(interop.Handle).AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
                {
                    handled = message is AppCommand or XButtonUp;
                    return 0;
                });
            };
            var handle = interop.EnsureHandle();

            nint SendCommand(int command) => SendMessage(handle, AppCommand, handle, unchecked(command << 16));
            // Exercise WPF's activation state without delivering WM_ACTIVATE to the default window procedure.
            void SetActive(bool active) => typeof(Window).GetMethod("HandleActivate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [active]);

            window.SetPageNavigationContext(whiteboard: true, presentation: true);
            Check(!window.IsActive && SendCommand(BrowserForward) == 0 && window.BoardPage == 1,
                "An inactive window should let browser commands pass through.");

            SetActive(true);
            Check(window.IsActive && !window.IsVisible,
                "The hidden test window should receive the simulated activation without being shown.");
            Check(SendCommand(BrowserForward | MouseDevice) == 1 && window.BoardPage == 2,
                "A Windows browser forward command should advance the board and mask the device flags.");
            Check(SendCommand(BrowserBackward) == 1 && window.BoardPage == 1,
                "A Windows browser backward command should return to the previous whiteboard page.");
            Check(SendCommand(BrowserRefresh) == 0 && SendMessage(handle, XButtonUp, BrowserForward << 16, 0) == 0 &&
                  window.BoardPage == 1 && window.PresentationPageChanges == 0,
                "An unrelated command or raw mouse release should pass through without turning pages.");

            window.SetPageNavigationContext(whiteboard: false, presentation: true);
            Check(SendCommand(BrowserForward) == 1 && window.PresentationPageOffset == 1,
                "A Windows browser forward command should advance the active slide show in desktop mode.");
            SetActive(false);
            Check(!window.IsActive && SendCommand(BrowserBackward) == 0 && window.PresentationPageOffset == 1,
                "The hidden window should stop responding to browser commands after deactivation.");
        }
        finally
        {
            window.Close();
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}

public partial class MainWindow
{
    private int currentMode;

    internal int BoardPage => CurrentWhiteboardIndex;
    internal int BoardPageCount => WhiteboardTotalCount;
    internal int PresentationPageOffset { get; private set; }
    internal int PresentationPageChanges { get; private set; }

    internal void SetPageNavigationContext(bool whiteboard, bool presentation)
    {
        currentMode = whiteboard ? 1 : 0;
        if (whiteboard && WhiteboardTotalCount == 1)
        {
            SwitchCanvasPage(1);
            BtnWhiteBoardAdd_Click(null, null);
            BtnWhiteBoardAdd_Click(null, null);
            SwitchCanvasPage(1);
        }
        SwitchCanvasPage(whiteboard ? CurrentWhiteboardIndex : 0);
        BtnPPTSlideShowEnd.Visibility = presentation ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void BindPageNavigationForTest() => PreviewKeyDown += (_, args) => args.Handled = HandlePageNavigationKey(args);

    private void BtnPPTSlidesUp_Click(object sender, RoutedEventArgs e)
    {
        PresentationPageOffset--;
        PresentationPageChanges++;
    }

    private void BtnPPTSlidesDown_Click(object sender, RoutedEventArgs e)
    {
        PresentationPageOffset++;
        PresentationPageChanges++;
    }
}
