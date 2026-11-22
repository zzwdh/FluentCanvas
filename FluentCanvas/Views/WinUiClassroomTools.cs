using System;
using System.Runtime.InteropServices;
using FluentCanvas.Localization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.Graphics;

namespace FluentCanvas.Views;

internal static class WinUiClassroomTools
{
    internal static Window CreateCountdown() => new WinUiCountdownWindow().Window;
    internal static Window CreateRandom(bool single) => new WinUiRandomWindow(single, Helpers.StorageHelper.UserDataFolder).Window;

    internal static Window CreateWindow(string title)
    {
        var window = new Window { Title = "FluentCanvas · " + title, SystemBackdrop = new MicaBackdrop() };
        SetAlwaysOnTop(window);
        return window;
    }

    internal static void SetAlwaysOnTop(Window window)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;
    }

    internal static void SetLanguage(FrameworkElement element)
    {
        var culture = UiText.ActiveCulture;
        element.Language = culture.Name;
        element.FlowDirection = culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    internal static void Resize(Window window, int width, int height, bool center = false)
    {
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest);
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(window));
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        int pixelsWide = (int)Math.Round(width * scale), pixelsHigh = (int)Math.Round(height * scale);
        if (area == null)
        {
            window.AppWindow.Resize(new SizeInt32(pixelsWide, pixelsHigh));
            return;
        }
        var work = area.WorkArea;
        pixelsWide = Math.Min(pixelsWide, work.Width);
        pixelsHigh = Math.Min(pixelsHigh, work.Height);
        var bounds = area.OuterBounds;
        var position = window.AppWindow.Position;
        int x = center ? work.X + (work.Width - pixelsWide) / 2
            : Math.Clamp(position.X - bounds.X, work.X, work.X + work.Width - pixelsWide);
        int y = center ? work.Y + (work.Height - pixelsHigh) / 2
            : Math.Clamp(position.Y - bounds.Y, work.Y, work.Y + work.Height - pixelsHigh);
        window.AppWindow.MoveAndResize(new RectInt32(x, y, pixelsWide, pixelsHigh), area);
    }

    internal static Button Button(string text, bool accent = false)
    {
        var button = new Button { Content = text, MinHeight = 40, Padding = new Thickness(14, 6, 14, 6) };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accentStyle)
            button.Style = accentStyle;
        return button;
    }

    internal static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };

    internal static void Announce(TextBlock label, string text)
    {
        label.Text = text;
        FrameworkElementAutomationPeer.FromElement(label)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
