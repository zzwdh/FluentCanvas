using FluentCanvas.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace FluentCanvas.Views;

internal static class WinUiSupport
{
    internal static Window CreateDiagnostics()
    {
        var details = $"FluentCanvas  {Assembly.GetExecutingAssembly().GetName().Version}\n" +
            $"{RuntimeInformation.FrameworkDescription}  ·  {RuntimeInformation.ProcessArchitecture}\n" +
            $"Windows  {Environment.OSVersion.Version}  ·  {RuntimeInformation.OSArchitecture}\n" +
            UiText.Format("Settings.InstallationMethod", App.IsPackaged ? "MSIX" : UiText.Get("Settings.Standalone")) + "\n" +
            UiText.Get("Settings.DiagnosticsComponents");

        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = UiText.Get("Settings.Diagnostics"), FontSize = 28, Style = TextStyle("TitleTextBlockStyle") });
        panel.Children.Add(new TextBlock { Text = details, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
        var copy = new Button { Content = UiText.Get("Settings.CopyDiagnostics") };
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(details);
                Clipboard.SetContent(data);
                Clipboard.Flush();
                copy.Content = UiText.Get("Settings.Copied");
            }
            catch (Exception)
            {
                copy.Content = UiText.Get("Settings.CopyFailed");
            }
        };
        panel.Children.Add(copy);
        return CreateWindow(UiText.Get("Settings.DiagnosticsTitle"), panel, 520, 410);
    }

    internal static Window CreateGuide()
    {
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 18 };
        panel.Children.Add(new TextBlock { Text = UiText.Get("Settings.Guide"), FontSize = 28, Style = TextStyle("TitleTextBlockStyle") });
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("Settings.GuideIntro"),
            TextWrapping = TextWrapping.Wrap
        });
        AddSection(panel, UiText.Get("Settings.GuideGlobal"),
            ("Alt + D", UiText.Get("Settings.GuidePen")),
            ("Alt + Q", UiText.Get("Settings.GuidePointer")),
            ("Alt + B", UiText.Get("Settings.HotkeyBoard")),
            ("Alt + V", UiText.Get("Settings.GuideTools")),
            ("Alt + C", UiText.Get("Settings.GuideCapture")),
            ("Ctrl + E", UiText.Get("Settings.GuideClear")),
            ("Shift + Esc", UiText.Get("Settings.HotkeyEndSlides")));
        AddSection(panel, UiText.Get("Settings.GuideCanvas"),
            ("Ctrl + Z", UiText.Get("Settings.GuideUndo")),
            ("Ctrl + Y", UiText.Get("Settings.GuideRedo")),
            ("Alt + S", UiText.Get("Settings.GuideSelect")),
            ("Alt + E", UiText.Get("Settings.GuideEraser")),
            ("Alt + L", UiText.Get("Settings.GuideLine")));
        AddSection(panel, UiText.Get("Settings.GuideSlides"),
            (UiText.Get("Settings.GuideNextKeys"), UiText.Get("Settings.NextSlide")),
            ("↑ / ← / Page Up / P", UiText.Get("Settings.PreviousSlide")),
            (UiText.Get("Settings.GuideWheel"), UiText.Get("Settings.GuideWheelSlides")),
            ("Esc", UiText.Get("Settings.HotkeyEndSlides")));
        return CreateWindow(UiText.Get("Settings.GuideTitle"), panel, 640, 620);
    }

    private static void AddSection(StackPanel panel, string title, params (string Keys, string Action)[] commands)
    {
        var section = new StackPanel { Spacing = 9 };
        section.Children.Add(new TextBlock { Text = title, FontSize = 18, Style = TextStyle("SubtitleTextBlockStyle") });
        foreach (var (keys, action) in commands)
        {
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(154) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var keyText = new TextBlock
            {
                Text = keys, TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            row.Children.Add(keyText);
            var actionText = new TextBlock { Text = action, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(actionText, 1);
            row.Children.Add(actionText);
            section.Children.Add(row);
        }
        panel.Children.Add(section);
    }

    private static Style TextStyle(string name) =>
        Application.Current.Resources.TryGetValue(name, out var value) ? value as Style : null;

    private static Window CreateWindow(string title, StackPanel panel, int width, int height)
    {
        var window = new Window
        {
            Title = title,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        window.AppWindow.Resize(new SizeInt32(width, height));
        return window;
    }
}
