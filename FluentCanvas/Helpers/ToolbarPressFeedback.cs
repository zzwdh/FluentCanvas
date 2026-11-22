using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluentCanvas.Helpers;

public static class ToolbarPressFeedback
{
    private const double PressedScale = 0.95;
    private static readonly Duration FeedbackDuration = TimeSpan.FromMilliseconds(83);

    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
        "IsPressed", typeof(bool), typeof(ToolbarPressFeedback), new PropertyMetadata(false, OnPressedChanged));

    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.RegisterAttached(
        "AnimationsEnabled", typeof(bool), typeof(ToolbarPressFeedback), new PropertyMetadata(false, OnPreferenceChanged));

    public static readonly DependencyProperty HighContrastProperty = DependencyProperty.RegisterAttached(
        "HighContrast", typeof(bool), typeof(ToolbarPressFeedback), new PropertyMetadata(false, OnPreferenceChanged));

    public static bool GetIsPressed(DependencyObject element) => (bool)element.GetValue(IsPressedProperty);
    public static void SetIsPressed(DependencyObject element, bool pressed) => element.SetValue(IsPressedProperty, pressed);

    public static bool GetAnimationsEnabled(DependencyObject element) => (bool)element.GetValue(AnimationsEnabledProperty);
    public static void SetAnimationsEnabled(DependencyObject element, bool enabled) => element.SetValue(AnimationsEnabledProperty, enabled);

    public static bool GetHighContrast(DependencyObject element) => (bool)element.GetValue(HighContrastProperty);
    public static void SetHighContrast(DependencyObject element, bool enabled) => element.SetValue(HighContrastProperty, enabled);

    private static bool CanAnimate(DependencyObject element) => GetAnimationsEnabled(element) && !GetHighContrast(element);

    private static void OnPreferenceChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (!CanAnimate(element)) ResetTransform(element);
    }

    private static void OnPressedChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement content) return;

        if (!content.IsLoaded || !CanAnimate(content))
        {
            ResetTransform(content);
            return;
        }

        if (content.RenderTransform is not ScaleTransform scale)
        {
            if (!(bool)args.NewValue) return;

            scale = new ScaleTransform();
            content.RenderTransform = scale;
        }

        double currentScale = scale.ScaleX;
        double targetScale = (bool)args.NewValue ? PressedScale : 1;
        scale.ScaleX = targetScale;
        scale.ScaleY = targetScale;

        var motion = new DoubleAnimation(currentScale, targetScale, FeedbackDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
    }

    private static void ResetTransform(DependencyObject element)
    {
        if (element is not FrameworkElement { RenderTransform: ScaleTransform scale }) return;

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
    }
}
