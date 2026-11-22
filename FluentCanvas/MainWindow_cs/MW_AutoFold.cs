using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        private bool isFloatingBarFolded;
        private bool isFloatingBarChangingHideMode;
        private bool isApplyingFloatingBarMode;
        private AnimationClock sidePanelMarginClock;

        private void FoldFloatingBar_Click(object sender, RoutedEventArgs e) =>
            QueueFloatingBarFold(folded: true, byUser: sender != null);

        private void UnFoldFloatingBar_MouseUp(object sender, MouseButtonEventArgs e) =>
            QueueFloatingBarFold(folded: false, byUser: sender != null);

        private void QueueFloatingBarFold(bool folded, bool byUser)
        {
            if (Dispatcher.HasShutdownStarted || (Dispatcher.CheckAccess() && isApplyingFloatingBarMode)) return;
            // Apply after the current action updates the canvas and presentation visibility.
            _ = Dispatcher.InvokeAsync(() => ApplyFloatingBarFold(folded, byUser));
        }

        private void ApplyFloatingBarFold(bool folded, bool byUser)
        {
            if (isApplyingFloatingBarMode) return;
            bool inPresentation = BtnPPTSlideShowEnd.Visibility == Visibility.Visible;
            foldFloatingBarByUser = folded && byUser;
            unfoldFloatingBarByUser = !folded && byUser;
            if (isFloatingBarFolded == folded) return;

            isApplyingFloatingBarMode = true;
            isFloatingBarChangingHideMode = true;
            isFloatingBarFolded = folded;
            try
            {
                if (folded)
                {
                    HideSubPanelsImmediately();
                    if (currentMode != 0) ImageBlackboard_Click(null, null);
                    if (foldFloatingBarByUser && StackPanelCanvasControls.Visibility == Visibility.Visible && inkCanvas.Strokes.Count > 2)
                        ShowNotificationAsync(UiText.Get("Actions.AutoFoldInk"));
                    CursorWithDelIcon_Click(null, null);
                    navigation.HidePanels();
                    HideSubPanels("cursor");
                }
                else if (inPresentation)
                {
                    navigation.ShowPanels(Settings.PowerPointSettings.IsShowBottomPPTNavigationPanel,
                        Settings.PowerPointSettings.IsShowSidePPTNavigationPanel);
                }

                ViewboxFloatingBarMarginAnimation();
                AnimateFoldHandles(folded);
            }
            finally
            {
                isApplyingFloatingBarMode = false;
                isFloatingBarChangingHideMode = sidePanelMarginClock != null;
            }
        }

        private void AnimateFoldHandles(bool folded)
        {
            double marginFromEdge = folded ? -16 : -40;
            var leftTarget = new Thickness(marginFromEdge, 0, 0, -150);
            var rightTarget = new Thickness(0, 0, marginFromEdge, -150);
            var leftCurrent = LeftSidePanel.Margin;
            var rightCurrent = RightSidePanel.Margin;

            if (folded) LeftSidePanel.Visibility = Visibility.Visible;
            LeftSidePanel.Margin = leftTarget;
            RightSidePanel.Margin = rightTarget;
            if (!AnimationsHelper.CanAnimate || !LeftSidePanel.IsVisible ||
                (leftCurrent == leftTarget && rightCurrent == rightTarget))
            {
                CompleteFoldHandleMotion(folded);
                return;
            }

            var clock = CreateToolbarMarginClock(leftCurrent, leftTarget, 167);
            sidePanelMarginClock = clock;
            clock.Completed += (_, _) =>
            {
                if (ReferenceEquals(sidePanelMarginClock, clock)) CompleteFoldHandleMotion(folded);
            };
            LeftSidePanel.ApplyAnimationClock(FrameworkElement.MarginProperty, clock);
            RightSidePanel.ApplyAnimationClock(FrameworkElement.MarginProperty,
                CreateToolbarMarginClock(rightCurrent, rightTarget, 167));
        }

        private void CompleteFoldHandleMotion(bool folded)
        {
            sidePanelMarginClock = null;
            LeftSidePanel.ApplyAnimationClock(FrameworkElement.MarginProperty, null);
            RightSidePanel.ApplyAnimationClock(FrameworkElement.MarginProperty, null);
            if (!folded) LeftSidePanel.Visibility = Visibility.Collapsed;
            isFloatingBarChangingHideMode = false;
        }

        private static AnimationClock CreateToolbarMarginClock(Thickness from, Thickness to, int milliseconds)
        {
            var motion = new ThicknessAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            motion.KeyFrames.Add(new DiscreteThicknessKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            motion.KeyFrames.Add(new SplineThicknessKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
                new KeySpline(0.55, 0.55, 0, 1)));
            return motion.CreateClock();
        }
    }
}
