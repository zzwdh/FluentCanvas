using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Wpf = System.Windows;

namespace FluentCanvas.Helpers;

internal static class PresentationWindow
{
    internal readonly record struct CaptureBounds(Rectangle VisibleClient, Rectangle Client, Rectangle Frame, Rectangle Window,
        uint ProcessId, uint ThreadId)
    {
        internal Rectangle Crop(Size capturedSize)
        {
            Rectangle source = Frame.Size == capturedSize ? Frame :
                Window.Size == capturedSize ? Window : Client.Size == capturedSize ? Client : Rectangle.Empty;
            if (!source.Contains(VisibleClient))
                throw new InvalidOperationException("The captured frame and the slide window bounds do not match.");
            return new Rectangle(VisibleClient.X - source.X, VisibleClient.Y - source.Y,
                VisibleClient.Width, VisibleClient.Height);
        }
    }

    internal static bool TryGetClientBounds(nint window, out Rectangle bounds)
    {
        if (!TryGetFullClientBounds(window, out bounds)) return false;
        bounds = Rectangle.Intersect(bounds, Screen.FromHandle(window).Bounds);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    internal static unsafe bool TryGetCaptureBounds(nint window, out CaptureBounds bounds)
    {
        bounds = default;
        uint thread = PInvoke.GetWindowThreadProcessId((HWND)window, out uint process);
        if (thread == 0) return false;
        if (!TryGetFullClientBounds(window, out var client) || !TryGetWindowBounds(window, out var outer))
            return false;

        var visibleClient = Rectangle.Intersect(client, Screen.FromHandle(window).Bounds);
        if (visibleClient.Width <= 0 || visibleClient.Height <= 0) return false;
        RECT frame;
        var visibleFrame = PInvoke.DwmGetWindowAttribute((HWND)window, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &frame, (uint)sizeof(RECT)).Succeeded ? Rectangle.FromLTRB(frame.left, frame.top, frame.right, frame.bottom) : Rectangle.Empty;
        bounds = new CaptureBounds(visibleClient, client, visibleFrame, outer, process, thread);
        return true;
    }

    internal static BitmapSource ComposeScreenshot(BitmapSource frame, CaptureBounds bounds, Wpf.FrameworkElement overlay)
    {
        var crop = bounds.Crop(new Size(frame.PixelWidth, frame.PixelHeight));
        var content = new CroppedBitmap(frame, new Wpf.Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
        if (!overlay.IsVisible || overlay.ActualWidth <= 0 || overlay.ActualHeight <= 0)
        {
            content.Freeze();
            return content;
        }

        var screen = bounds.VisibleClient;
        var topLeft = overlay.PointFromScreen(new Wpf.Point(screen.Left, screen.Top));
        var bottomRight = overlay.PointFromScreen(new Wpf.Point(screen.Right, screen.Bottom));
        var annotation = new VisualBrush(overlay)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Wpf.Rect(topLeft, bottomRight),
            Stretch = Stretch.Fill,
        };
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var imageRect = new Wpf.Rect(0, 0, crop.Width, crop.Height);
            drawing.DrawImage(content, imageRect);
            drawing.DrawRectangle(annotation, null, imageRect);
        }
        var composed = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        composed.Render(visual);
        composed.Freeze();
        return composed;
    }

    private static bool TryGetFullClientBounds(nint window, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        var handle = (HWND)window;
        if (window == 0 || !IsDisplayed(handle) ||
            !PInvoke.GetClientRect(handle, out RECT client))
            return false;

        Point origin = Point.Empty;
        if (!PInvoke.ClientToScreen(handle, ref origin)) return false;

        bounds = new Rectangle(origin.X, origin.Y, client.Width, client.Height);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    internal static bool TryGetWindowBounds(nint window, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (window == 0 || !PInvoke.GetWindowRect((HWND)window, out RECT rect)) return false;
        bounds = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    internal static bool TryMove(nint window, Rectangle bounds, bool aboveShow = false)
    {
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        if (!aboveShow) flags |= SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
        var insertAfter = aboveShow ? (HWND)(nint)(-1) : default; // HWND_TOPMOST
        return PInvoke.SetWindowPos((HWND)window, insertAfter, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags);
    }

    private static unsafe bool IsDisplayed(HWND window)
    {
        if (!PInvoke.IsWindowVisible(window) || PInvoke.IsIconic(window)) return false;
        uint cloaked = 0;
        return !PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Succeeded ||
            cloaked == 0;
    }
}
