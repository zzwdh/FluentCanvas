using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluentCanvas.Helpers
{
    internal static class AnimationsHelper
    {
        private const double PanelOffset = 8;
        private const double PanelDuration = 0.167;
        private static readonly Vector BottomOffset = new(0, PanelOffset);
        private static readonly Vector LeftOffset = new(-PanelOffset, 0);
        private static readonly Vector RightOffset = new(PanelOffset, 0);
        private static readonly ConditionalWeakTable<FrameworkElement, PanelTransition> Transitions = new();

        internal static bool CanAnimate => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

        public static bool IsShown(FrameworkElement element) =>
            Transitions.TryGetValue(element, out var transition)
                ? transition.IsShowing : element.Visibility == Visibility.Visible;

        public static void ShowWithSlideFromBottomAndFade(FrameworkElement element, double duration = PanelDuration) =>
            SetPanelVisibility(element, true, BottomOffset, duration);

        public static void ShowWithSlideFromLeftAndFade(FrameworkElement element) =>
            SetPanelVisibility(element, true, LeftOffset, PanelDuration);

        public static void ShowWithSlideFromRightAndFade(FrameworkElement element) =>
            SetPanelVisibility(element, true, RightOffset, PanelDuration);

        public static void HideWithSlideAndFade(FrameworkElement element) =>
            SetPanelVisibility(element, false, BottomOffset, PanelDuration);

        public static void HideToLeft(FrameworkElement element) =>
            SetPanelVisibility(element, false, LeftOffset, PanelDuration);

        public static void HideToRight(FrameworkElement element) =>
            SetPanelVisibility(element, false, RightOffset, PanelDuration);

        public static void HideImmediately(FrameworkElement element)
        {
            if (Transitions.TryGetValue(element, out var transition)) transition.Stop();
            element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        }

        private static void SetPanelVisibility(FrameworkElement element, bool show, Vector offset, double duration)
        {
            Transitions.TryGetValue(element, out var previous);
            if (!CanAnimate || !element.IsLoaded)
            {
                previous?.Stop();
                element.SetCurrentValue(UIElement.VisibilityProperty, show ? Visibility.Visible : Visibility.Collapsed);
                return;
            }

            if (IsShown(element) == show) return;

            double opacity = previous != null ? element.Opacity : show ? 0 : element.Opacity;
            Vector translation = previous?.Offset ?? (show ? offset : new Vector());
            previous?.Stop();

            if (show) element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            if (!element.IsVisible)
            {
                if (!show) HideImmediately(element);
                return;
            }

            new PanelTransition(element, show, translation, show ? new Vector() : offset, opacity,
                TimeSpan.FromSeconds(duration)).Start();
        }

        private sealed class PanelTransition(
            FrameworkElement element, bool show, Vector from, Vector to, double opacity, TimeSpan duration)
        {
            private readonly Storyboard storyboard = new();
            private readonly Transform originalTransform = element.RenderTransform;
            private readonly TranslateTransform translation = new(from.X, from.Y);
            private TransformGroup transform;

            public bool IsShowing => show;
            public Vector Offset => new(translation.X, translation.Y);

            public void Start()
            {
                transform = new TransformGroup();
                transform.Children.Add(originalTransform);
                transform.Children.Add(translation);

                var endOpacity = show ? (double)element.GetAnimationBaseValue(UIElement.OpacityProperty) : 0;
                AddAnimation(new DoubleAnimation(opacity, endOpacity, show ? TimeSpan.FromMilliseconds(83) : duration),
                    element, UIElement.OpacityProperty);
                AddSlide(from.X, to.X, TranslateTransform.XProperty);
                AddSlide(from.Y, to.Y, TranslateTransform.YProperty);
                if (!show)
                {
                    // Keep a disappearing flyout from consuming input meant for the canvas.
                    var hitTest = new BooleanAnimationUsingKeyFrames();
                    hitTest.KeyFrames.Add(new DiscreteBooleanKeyFrame(false, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                    AddAnimation(hitTest, element, UIElement.IsHitTestVisibleProperty);
                }

                Transitions.Add(element, this);
                element.SetCurrentValue(UIElement.RenderTransformProperty, transform);
                storyboard.Completed += Finish;
                element.IsVisibleChanged += OnVisibilityChanged;
                element.Unloaded += Finish;
                SystemParameters.StaticPropertyChanged += OnSystemPreferenceChanged;
                storyboard.Begin(element, HandoffBehavior.SnapshotAndReplace, isControllable: true);
            }

            private void AddSlide(double start, double end, DependencyProperty property)
            {
                if (start == end) return;
                var slide = new DoubleAnimationUsingKeyFrames { Duration = duration };
                slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                slide.KeyFrames.Add(new SplineDoubleKeyFrame(end, KeyTime.FromTimeSpan(duration), new KeySpline(0, 0, 0, 1)));
                AddAnimation(slide, translation, property);
            }

            private void AddAnimation(Timeline animation, DependencyObject target, DependencyProperty property)
            {
                Storyboard.SetTarget(animation, target);
                Storyboard.SetTargetProperty(animation, new PropertyPath(property));
                storyboard.Children.Add(animation);
            }

            public void Stop()
            {
                Transitions.Remove(element);
                SystemParameters.StaticPropertyChanged -= OnSystemPreferenceChanged;
                element.IsVisibleChanged -= OnVisibilityChanged;
                element.Unloaded -= Finish;
                storyboard.Completed -= Finish;
                storyboard.Remove(element);
                if (ReferenceEquals(element.RenderTransform, transform))
                    element.SetCurrentValue(UIElement.RenderTransformProperty, originalTransform);
            }

            private void Finish(object sender, EventArgs e)
            {
                if (!Transitions.TryGetValue(element, out var current) || current != this) return;
                Stop();
                if (!show && element.Visibility == Visibility.Visible)
                    element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            }

            private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (!element.IsVisible) Finish(sender, EventArgs.Empty);
            }

            private void OnSystemPreferenceChanged(object sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))) return;
                if (element.Dispatcher.CheckAccess()) RespectSystemPreference();
                else if (!element.Dispatcher.HasShutdownStarted) _ = element.Dispatcher.InvokeAsync(RespectSystemPreference);
            }

            private void RespectSystemPreference()
            {
                if (!CanAnimate) Finish(null, EventArgs.Empty);
            }
        }
    }
}
