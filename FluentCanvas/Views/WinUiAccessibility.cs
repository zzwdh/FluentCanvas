using System;
using System.Runtime.Versioning;
using Microsoft.UI.Dispatching;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace FluentCanvas.Views;

internal sealed class WinUiAccessibility
{
    private readonly UISettings systemSettings = new();
    private readonly ThemeSettings themeSettings;
    private readonly DispatcherQueue dispatcher;
    private Border card;
    private UIElement entrance;
    private Storyboard reveal;
    private bool closed;

    internal WinUiAccessibility(Window window)
    {
        dispatcher = window.DispatcherQueue;
        themeSettings = ThemeSettings.CreateForWindowId(window.AppWindow.Id);
        window.Closed += OnClosed;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            systemSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        else
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        themeSettings.Changed += OnThemeChanged;
    }

    internal bool AnimationsEnabled => systemSettings.AnimationsEnabled && !themeSettings.HighContrast;

    internal Border CreateCard(UIElement child, Thickness padding)
    {
        card = new Border
        {
            Child = child,
            Padding = padding,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1)
        };
        card.ActualThemeChanged += OnCardThemeChanged;
        systemSettings.ColorValuesChanged += OnColorValuesChanged;
        UpdateCardPalette();
        return card;
    }

    private void UpdateCardPalette()
    {
        if (card == null) return;

        if (themeSettings.HighContrast)
        {
            card.Background = new SolidColorBrush(systemSettings.UIElementColor(UIElementType.Window));
            card.BorderBrush = new SolidColorBrush(systemSettings.UIElementColor(UIElementType.WindowText));
            return;
        }

        var resources = Application.Current.Resources;
        card.Background = resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out var background) ? background as Brush : null;
        card.BorderBrush = resources.TryGetValue("CardStrokeColorDefaultBrush", out var stroke) ? stroke as Brush : null;
    }

    internal void AddEntrance(UIElement element)
    {
        entrance = element;
        UpdateEntrance();
    }

    private void UpdateEntrance()
    {
        if (entrance == null) return;

        entrance.Transitions = AnimationsEnabled ? new TransitionCollection
        {
            new EntranceThemeTransition
            {
                FromHorizontalOffset = 0,
                FromVerticalOffset = 12,
                IsStaggeringEnabled = false
            }
        } : null;
    }

    internal void Reveal(UIElement element)
    {
        StopReveal();
        if (!AnimationsEnabled) return;

        var fade = new DoubleAnimation
        {
            From = 0.45,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(83))
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));

        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        storyboard.Children.Add(fade);
        storyboard.Completed += OnRevealCompleted;
        reveal = storyboard;
        storyboard.Begin();
    }

    private void OnRevealCompleted(object sender, object args)
    {
        if (sender is not Storyboard storyboard) return;
        storyboard.Completed -= OnRevealCompleted;
        if (reveal == storyboard) reveal = null;
    }

    private void StopReveal()
    {
        if (reveal == null) return;
        reveal.Completed -= OnRevealCompleted;
        reveal.Stop();
        reveal = null;
    }

    [SupportedOSPlatform("windows10.0.19041")]
    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
        dispatcher.TryEnqueue(RefreshMotionPreference);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) =>
        dispatcher.TryEnqueue(RefreshMotionPreference);

    private void RefreshMotionPreference()
    {
        if (closed) return;
        UpdateEntrance();
        if (!AnimationsEnabled) StopReveal();
    }

    private void OnThemeChanged(ThemeSettings sender, object args)
    {
        dispatcher.TryEnqueue(() =>
        {
            if (closed) return;
            UpdateCardPalette();
            RefreshMotionPreference();
        });
    }

    private void OnCardThemeChanged(FrameworkElement sender, object args) => UpdateCardPalette();

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        dispatcher.TryEnqueue(() =>
        {
            if (!closed) UpdateCardPalette();
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            systemSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        else
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        themeSettings.Changed -= OnThemeChanged;
        if (card != null)
        {
            systemSettings.ColorValuesChanged -= OnColorValuesChanged;
            card.ActualThemeChanged -= OnCardThemeChanged;
        }
        StopReveal();
    }
}
