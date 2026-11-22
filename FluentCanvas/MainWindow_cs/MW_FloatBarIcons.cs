using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using FluentCanvas.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Threading;
using Application = System.Windows.Application;
using Point = System.Windows.Point;
using System.Diagnostics;
using iNKORE.UI.WPF.Modern.Controls;
using System.Collections.Generic;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        #region TwoFingZoomBtn

        private void TwoFingerGestureBorder_Click(object sender, RoutedEventArgs e) =>
            ToggleToolbarPanel(TwoFingerGestureBorder, BoardTwoFingerGestureBorder);

        private void CheckEnableTwoFingerGestureBtnColorPrompt()
        {
            if (ToggleSwitchEnableMultiTouchMode.IsOn)
            {
                TwoFingerGestureSimpleStackPanel.Opacity = 0.5;
                EnableTwoFingerGestureBtn.Opacity = 0.5;
            }
            else
            {
                TwoFingerGestureSimpleStackPanel.Opacity = 1;
                if (IsTwoFingerGestureActive)
                {
                    EnableTwoFingerGestureBtn.Opacity = 1;
                }
                else
                {
                    EnableTwoFingerGestureBtn.Opacity = 0.5;
                }
            }
        }

        private void CheckEnableTwoFingerGestureBtnVisibility(bool isVisible)
        {
            if (StackPanelCanvasControls.Visibility != Visibility.Visible
                || BorderFloatingBarMainControls.Visibility != Visibility.Visible)
            {
                EnableTwoFingerGestureBorder.Visibility = Visibility.Collapsed;
            }
            else if (isVisible == true)
            {
                if (BtnPPTSlideShowEnd.Visibility == Visibility.Visible) EnableTwoFingerGestureBorder.Visibility = Visibility.Collapsed;
                else EnableTwoFingerGestureBorder.Visibility = Visibility.Visible;
            }
            else EnableTwoFingerGestureBorder.Visibility = Visibility.Collapsed;
        }

        #endregion TwoFingZoomBtn

        #region Drag

        bool isDragDropInEffect = false;
        Point pos = new Point();
        Point downPos = new Point();
        private Point? desktopToolbarPosition;
        private Point? presentationToolbarPosition;

        private void FloatingToolbarDrag_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDragDropInEffect)
            {
                bool inPresentation = BtnPPTSlideShowEnd.Visibility == Visibility.Visible;
                var area = GetFloatingToolbarArea(inPresentation);
                if (area.IsEmpty) return;

                var currentPointer = e.GetPosition(null);
                var currentMargin = ViewboxFloatingBar.Margin;
                var position = ClampFloatingToolbarPosition(
                    new Point(currentMargin.Left, currentMargin.Top) + (currentPointer - pos), area);
                ViewboxFloatingBar.Margin = new Thickness(position.X, position.Y, -2000, -200);
                if (inPresentation) presentationToolbarPosition = position;
                else desktopToolbarPosition = position;
                pos = currentPointer;
            }
        }

        private void FloatingToolbarDrag_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!Topmost || isFloatingBarFolded) return;

            pendingFloatingToolbarMargin?.Abort();
            if (floatingToolbarMarginClock != null)
            {
                var current = ViewboxFloatingBar.Margin;
                floatingToolbarMarginClock = null;
                ViewboxFloatingBar.Margin = current;
                ViewboxFloatingBar.ApplyAnimationClock(FrameworkElement.MarginProperty, null);
            }
            isDragDropInEffect = true;
            pos = e.GetPosition(null);
            downPos = e.GetPosition(null);
            GridForFloatingBarDraging.Visibility = Visibility.Visible;
        }

        private void ToggleFloatingToolbar_Click(object sender, RoutedEventArgs e) => FloatingToolbarDrag_MouseUp(sender, null);

        private void FloatingToolbarDrag_MouseUp(object sender, MouseButtonEventArgs e)
        {
            isDragDropInEffect = false;

            if (e is null || Math.Abs(downPos.X - e.GetPosition(null).X) <= 10 && Math.Abs(downPos.Y - e.GetPosition(null).Y) <= 10)
            {
                if (BorderFloatingBarMainControls.Visibility == Visibility.Visible)
                {
                    AnimationsHelper.HideImmediately(BorderFloatingBarMainControls);
                    CheckEnableTwoFingerGestureBtnVisibility(false);
                }
                else
                {
                    AnimationsHelper.ShowWithSlideFromBottomAndFade(BorderFloatingBarMainControls);
                    CheckEnableTwoFingerGestureBtnVisibility(true);
                }
            }

            GridForFloatingBarDraging.Visibility = Visibility.Collapsed;
            ViewboxFloatingBarMarginAnimation();
        }

        #endregion

        private string activeToolbarTool;
        private bool IsPenToolActive => activeToolbarTool is "pen" or "color";

        private void HideSubPanelsImmediately()
        {
            RestoreToolbarFlyoutFocus();
            AnimationsHelper.HideImmediately(BorderTools);
            AnimationsHelper.HideImmediately(BoardBorderTools);
            AnimationsHelper.HideImmediately(PenPalette);
            AnimationsHelper.HideImmediately(BoardPenPalette);
            AnimationsHelper.HideImmediately(BoardDeleteIcon);
        }

        private async void HideSubPanels(String mode = null, bool autoAlignCenter = false)
        {
            RestoreToolbarFlyoutFocus();
            AnimationsHelper.HideWithSlideAndFade(BorderTools);
            AnimationsHelper.HideWithSlideAndFade(BoardBorderTools);
            AnimationsHelper.HideWithSlideAndFade(PenPalette);
            AnimationsHelper.HideWithSlideAndFade(BoardPenPalette);
            AnimationsHelper.HideWithSlideAndFade(BoardDeleteIcon);
            AnimationsHelper.HideWithSlideAndFade(TwoFingerGestureBorder);
            AnimationsHelper.HideWithSlideAndFade(BoardTwoFingerGestureBorder);
            if (ToggleSwitchDrawShapeBorderAutoHide.IsOn)
            {
                AnimationsHelper.HideWithSlideAndFade(BorderDrawShape);
                AnimationsHelper.HideWithSlideAndFade(BoardBorderDrawShape);
            }

            if (mode != null)
            {
                if (mode != "clear")
                {
                    activeToolbarTool = mode;
                    BoardPen.SetResourceReference(Border.BackgroundProperty, "BoardBarBackground");
                    BoardPen.Opacity = 1;
                    BoardEraser.SetResourceReference(Border.BackgroundProperty, "BoardBarBackground");
                    BoardEraser.Opacity = 1;
                    BoardSelect.SetResourceReference(Border.BackgroundProperty, "BoardBarBackground");
                    BoardSelect.Opacity = 1;
                    BoardEraserByStrokes.SetResourceReference(Border.BackgroundProperty, "BoardBarBackground");
                    BoardEraserByStrokes.Opacity = 1;

                    ToolbarSelection.SetIsActive(DesktopToolbarCursorButton, mode == "cursor");
                    SetToolbarToolStatus(BoardToolbarPenButton, DesktopToolbarPenButton, IsPenToolActive);
                    SetToolbarToolStatus(BoardToolbarEraserButton, DesktopToolbarEraserButton, mode == "eraser");
                    SetToolbarToolStatus(BoardToolbarEraseStrokesButton, DesktopToolbarEraseStrokesButton, mode == "eraserByStrokes");
                    SetToolbarToolStatus(BoardToolbarSelectButton, DesktopToolbarSelectButton, mode == "select");
                }
                if (mode == "pen" || mode == "color")
                {
                    BoardPen.Background = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/Resources/Icons-png/check-box-background.png"))) { Opacity = 0.5 };
                    BoardPen.Opacity = 0.99;
                }
                else
                {
                    if (mode == "eraser")
                    {
                        BoardEraser.Background = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/Resources/Icons-png/check-box-background.png"))) { Opacity = 0.5 };
                        BoardEraser.Opacity = 0.99;
                    }
                    else if (mode == "eraserByStrokes")
                    {
                        BoardEraserByStrokes.Background = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/Resources/Icons-png/check-box-background.png"))) { Opacity = 0.5 };
                        BoardEraserByStrokes.Opacity = 0.99;
                    }
                    else if (mode == "select")
                    {
                        BoardSelect.Background = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/Resources/Icons-png/check-box-background.png"))) { Opacity = 0.5 };
                    }
                }

                if (autoAlignCenter) // 控制居中
                {
                    await Task.Delay(50);
                    ViewboxFloatingBarMarginAnimation();
                }
            }
            await Task.Delay(150);
            isHidingSubPanelsWhenInking = false;
        }

        private static void SetToolbarToolStatus(Button board, Button desktop, bool selected)
        {
            ToolbarSelection.SetIsActive(board, selected);
            ToolbarSelection.SetIsActive(desktop, selected);
        }

        private void SymbolIconUndo_Click(object sender, RoutedEventArgs e)
        {
            if (!timeMachine.CanUndo) return;
            BtnUndo_Click(null, null);
            HideSubPanels();
        }

        private void SymbolIconRedo_Click(object sender, RoutedEventArgs e)
        {
            if (!timeMachine.CanRedo) return;
            BtnRedo_Click(null, null);
            HideSubPanels();
        }

        private async void SymbolIconCursor_Click(object sender, RoutedEventArgs e)
        {
            if (currentMode != 0)
            {
                ImageBlackboard_Click(null, null);
            }
            else
            {
                BtnHideInkCanvas_Click(null, null);

                if (BtnPPTSlideShowEnd.Visibility == Visibility.Visible)
                {
                    await Task.Delay(100);
                    ViewboxFloatingBarMarginAnimation();
                }
            }
        }

        private void SymbolIconDelete_MouseUp(object sender, RoutedEventArgs e)
        {
            var selectedStrokes = inkCanvas.GetSelectedStrokes();
            var selectedElements = new List<UIElement>(inkCanvas.GetSelectedElements());
            if (selectedStrokes.Count > 0 || selectedElements.Count > 0)
            {
                inkCanvas.Strokes.Remove(inkCanvas.GetSelectedStrokes());
                foreach(UIElement element in selectedElements)
                {
                    inkCanvas.Children.Remove(element);
                    timeMachine.CommitElementInsertHistory(element, true);
                }
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
            }
            else if (HasCanvasContent) BtnClear_Click(null, null);
        }

        private void SymbolIconSettings_Click(object sender, RoutedEventArgs e)
        {
            HideSubPanels();
            BtnSettings_Click(null, null);
        }

        private void SymbolIconSelect_Click(object sender, RoutedEventArgs e)
        {
            BtnSelect_Click(null, null);
            HideSubPanels("select");
        }

        private async void SymbolIconScreenshot_Click(object sender, RoutedEventArgs e)
        {
            HideSubPanelsImmediately();
            await Task.Delay(50);
            SaveScreenShotToDesktop();
        }

        bool isDisplayingOrHidingBlackboard = false;
        private void ImageBlackboard_Click(object sender, RoutedEventArgs e)
        {
            if (isDisplayingOrHidingBlackboard) return;
            isDisplayingOrHidingBlackboard = true;

            if (inkCanvas.EditingMode == InkCanvasEditingMode.Select) PenIcon_Click(null, null);

            if (currentMode == 0)
            {
                currentMode = 1;
                //进入画板
                navigation.HidePanels();

                new Thread(new ThreadStart(() =>
                {
                    Thread.Sleep(100);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ViewboxFloatingBarMarginAnimation();
                    });
                })).Start();

                if (!IsPenToolActive)
                {
                    PenIcon_Click(BoardPenIcon, null);
                }
            }
            else
            {
                ArchiveCanvasForTransition();
                currentMode = 0;
                //退出画板
                HideSubPanelsImmediately();

                if (BtnPPTSlideShowEnd.Visibility == Visibility.Visible)
                {
                    navigation.ShowPanels(
                        Settings.PowerPointSettings.IsShowBottomPPTNavigationPanel,
                        Settings.PowerPointSettings.IsShowSidePPTNavigationPanel);
                }

                ViewboxFloatingBarMarginAnimation();

                if (!IsPenToolActive)
                {
                    PenIcon_Click(null, null);
                }
            }

            ApplySceneGestureSettings();
            BtnSwitch_Click(null, null);

            if (currentMode == 0 && inkCanvas.Strokes.Count == 0 && BtnPPTSlideShowEnd.Visibility != Visibility.Visible)
            {
                CursorIcon_Click(null, null);
            }

            ApplyCanvasTheme();

            new Thread(new ThreadStart(() =>
            {
                Thread.Sleep(200);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    isDisplayingOrHidingBlackboard = false;
                });
            })).Start();

            CheckColorTheme(true);
        }

        private void ImageCountdownTimer_Click(object sender, RoutedEventArgs e)
        {
            OpenWinUiTool("--countdown");
        }

        private void OperatingGuideWindowIcon_Click(object sender, RoutedEventArgs e)
        {
            OpenWinUiTool("--guide");
        }

        private void SymbolIconRand_Click(object sender, RoutedEventArgs e)
        {
            OpenWinUiTool("--random");
        }

        private void OpenWinUiTool(string action)
        {
            RestoreToolbarFlyoutFocus();
            AnimationsHelper.HideWithSlideAndFade(BorderTools);
            AnimationsHelper.HideWithSlideAndFade(BoardBorderTools);
            try { WinUiWindowLauncher.Open(action); }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
                ShowNotificationAsync(UiText.Get("Actions.OpenToolFailed"));
            }
        }

        private bool singleDrawOpen;

        private async void SymbolIconRandOne_Click(object sender, RoutedEventArgs e)
        {
            if (singleDrawOpen) return;
            RestoreToolbarFlyoutFocus();
            AnimationsHelper.HideWithSlideAndFade(BorderTools);
            AnimationsHelper.HideWithSlideAndFade(BoardBorderTools);
            singleDrawOpen = true;
            bool wasEnabled = IsEnabled;
            try
            {
                using var child = WinUiWindowLauncher.Start("--random-single");
                IsEnabled = false;
                await child.WaitForExitAsync();
                if (child.ExitCode != 0) ShowNotificationAsync(UiText.Get("Actions.OpenDrawOneFailed"));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
                ShowNotificationAsync(UiText.Get("Actions.OpenDrawOneFailed"));
            }
            finally
            {
                IsEnabled = wasEnabled;
                singleDrawOpen = false;
            }
        }

        private InkReplaySession inkReplay;
        private long inkReplayStartedAt;

        private void GridInkReplayButton_Click(object sender, RoutedEventArgs e)
        {
            RestoreToolbarFlyoutFocus();
            AnimationsHelper.HideWithSlideAndFade(BorderTools);
            AnimationsHelper.HideWithSlideAndFade(BoardBorderTools);

            CollapseBorderDrawShape();

            StopInkReplay();
            var selected = inkCanvas.GetSelectedStrokes();
            var source = selected.Count == 0 ? inkCanvas.Strokes : selected;
            inkReplay = new InkReplaySession(source.Clone(), InkCanvasForInkReplay.Strokes);
            inkReplayStartedAt = Stopwatch.GetTimestamp();
            InkCanvasForInkReplay.Visibility = Visibility.Visible;
            inkCanvas.Visibility = Visibility.Collapsed;
            Closed += InkReplayWindow_Closed;
            CompositionTarget.Rendering += InkReplay_Rendering;
        }

        private void InkReplay_Rendering(object sender, EventArgs e)
        {
            if (inkReplay?.Advance(Stopwatch.GetElapsedTime(inkReplayStartedAt)) == true) StopInkReplay();
        }

        private void InkReplayWindow_Closed(object sender, EventArgs e) => StopInkReplay();

        private void StopInkReplay()
        {
            if (inkReplay == null) return;
            CompositionTarget.Rendering -= InkReplay_Rendering;
            Closed -= InkReplayWindow_Closed;
            inkReplay = null;
            InkCanvasForInkReplay.Visibility = Visibility.Collapsed;
            InkCanvasForInkReplay.Strokes.Clear();
            inkCanvas.Visibility = Visibility.Visible;
        }

        private void InkCanvasForInkReplay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) StopInkReplay();
        }

        private void SymbolIconTools_Click(object sender, RoutedEventArgs e)
        {
            ToggleToolbarFlyout(BorderTools, BoardBorderTools, DesktopToolbarMoreButton, BoardToolbarMoreButton,
                DesktopToolsFirstButton, BoardToolsFirstButton);
        }

        private void ToggleToolbarFlyout(FrameworkElement desktop, FrameworkElement board, Button desktopOpener, Button boardOpener,
            Button desktopFirst, Button boardFirst)
        {
            bool onDesktop = currentMode == 0;
            if (!ToggleToolbarPanel(desktop, board)) return;

            var opener = onDesktop ? desktopOpener : boardOpener;
            var first = onDesktop ? desktopFirst : boardFirst;
            var activePanel = onDesktop ? desktop : board;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (opener.IsKeyboardFocused && first.IsVisible && AnimationsHelper.IsShown(activePanel)) first.Focus();
            }, DispatcherPriority.Input);
        }

        private bool ToggleToolbarPanel(FrameworkElement desktop, FrameworkElement board)
        {
            if (AnimationsHelper.IsShown(currentMode == 0 ? desktop : board))
            {
                RestoreToolbarFlyoutFocus();
                AnimationsHelper.HideWithSlideAndFade(desktop);
                AnimationsHelper.HideWithSlideAndFade(board);
                return false;
            }

            AnimationsHelper.ShowWithSlideFromBottomAndFade(desktop);
            AnimationsHelper.ShowWithSlideFromBottomAndFade(board);
            return true;
        }

        private void RestoreToolbarFlyoutFocus()
        {
            if (BorderTools.IsKeyboardFocusWithin) DesktopToolbarMoreButton.Focus();
            else if (BoardBorderTools.IsKeyboardFocusWithin) BoardToolbarMoreButton.Focus();
            else if (PenPalette.IsKeyboardFocusWithin) DesktopToolbarPenButton.Focus();
            else if (BoardPenPalette.IsKeyboardFocusWithin) BoardToolbarPenButton.Focus();
        }

        private bool DismissToolbarFlyout()
        {
            var tools = currentMode == 0 ? BorderTools : BoardBorderTools;
            var palette = currentMode == 0 ? PenPalette : BoardPenPalette;
            bool paletteOpen = palette.IsVisible && AnimationsHelper.IsShown(palette);
            if (tools.IsVisible && AnimationsHelper.IsShown(tools) && (tools.IsKeyboardFocusWithin || !paletteOpen))
            {
                (currentMode == 0 ? DesktopToolbarMoreButton : BoardToolbarMoreButton).Focus();
                AnimationsHelper.HideImmediately(BorderTools);
                AnimationsHelper.HideImmediately(BoardBorderTools);
                return true;
            }
            if (!paletteOpen) return false;

            (currentMode == 0 ? DesktopToolbarPenButton : BoardToolbarPenButton).Focus();
            AnimationsHelper.HideImmediately(PenPalette);
            AnimationsHelper.HideImmediately(BoardPenPalette);
            return true;
        }

        private DispatcherOperation pendingFloatingToolbarMargin;
        private AnimationClock floatingToolbarMarginClock;

        private void InitFloatingToolbarPlacement()
        {
            Main_Grid.SizeChanged += (_, _) => ViewboxFloatingBarMarginAnimation();
            ViewboxFloatingBar.SizeChanged += (_, _) => ViewboxFloatingBarMarginAnimation();
            LocationChanged += (_, _) => ViewboxFloatingBarMarginAnimation();
            StateChanged += (_, _) => ViewboxFloatingBarMarginAnimation();
            DpiChanged += (_, _) => ViewboxFloatingBarMarginAnimation();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        }

        private void DisplaySettingsChanged(object sender, EventArgs e) => ViewboxFloatingBarMarginAnimation();

        private Rect GetFloatingToolbarArea(bool inPresentation)
        {
            if (WindowState == WindowState.Minimized ||
                PresentationSource.FromVisual(Main_Grid) is not HwndSource { Handle: not 0 } source)
                return Rect.Empty;

            var screen = System.Windows.Forms.Screen.FromHandle(source.Handle);
            var screenArea = inPresentation ? screen.Bounds : screen.WorkingArea;
            // Convert both corners because the window and work area can have different origins.
            var area = new Rect(Main_Grid.PointFromScreen(new Point(screenArea.Left, screenArea.Top)),
                Main_Grid.PointFromScreen(new Point(screenArea.Right, screenArea.Bottom)));
            area.Intersect(new Rect(Main_Grid.RenderSize));
            return area;
        }

        private Size FloatingToolbarSize => new(ViewboxFloatingBar.ActualWidth * ViewboxFloatingBarScaleTransform.ScaleX,
            ViewboxFloatingBar.ActualHeight * ViewboxFloatingBarScaleTransform.ScaleY);

        private Point ClampFloatingToolbarPosition(Point position, Rect area)
        {
            var size = FloatingToolbarSize;
            return new Point(Math.Clamp(position.X, area.Left, Math.Max(area.Left, area.Right - size.Width)),
                Math.Clamp(position.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));
        }

        private void ViewboxFloatingBarMarginAnimation()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(ViewboxFloatingBarMarginAnimation);
                return;
            }

            if (pendingFloatingToolbarMargin is { Status: DispatcherOperationStatus.Pending }) return;
            pendingFloatingToolbarMargin = Dispatcher.InvokeAsync(UpdateFloatingToolbarMargin, DispatcherPriority.Loaded);
        }

        private void UpdateFloatingToolbarMargin()
        {
            pendingFloatingToolbarMargin = null;
            bool showToolbar = Topmost && !isFloatingBarFolded;
            if (isDragDropInEffect)
            {
                if (showToolbar) return;
                isDragDropInEffect = false;
                GridForFloatingBarDraging.Visibility = Visibility.Collapsed;
            }

            bool inPresentation = BtnPPTSlideShowEnd.Visibility == Visibility.Visible;
            var area = GetFloatingToolbarArea(inPresentation);
            if (area.IsEmpty) return;

            double marginFromEdge = isFloatingBarFolded ? -100 : inPresentation ? 60 : Settings.Appearance.FloatingBarBottomMargin;
            marginFromEdge = Topmost ? marginFromEdge * (Settings.Appearance.FloatingBarScale / 100) : -60;
            if (showToolbar) ViewboxFloatingBar.Visibility = Visibility.Visible;

            var current = ViewboxFloatingBar.Margin;
            var position = new Point(showToolbar ? area.Left + (area.Width - FloatingToolbarSize.Width) / 2 : current.Left,
                area.Bottom - marginFromEdge * (ViewboxFloatingBarScaleTransform.ScaleY == 1 ? 1 : 0.9));
            if (showToolbar)
            {
                var saved = inPresentation ? presentationToolbarPosition : desktopToolbarPosition;
                position = ClampFloatingToolbarPosition(saved ?? position, area);
            }

            var target = new Thickness(position.X, position.Y, -2000, -200);
            bool canAnimate = AnimationsHelper.CanAnimate && ViewboxFloatingBar.IsVisible;
            if (canAnimate && floatingToolbarMarginClock != null &&
                (Thickness)ViewboxFloatingBar.GetAnimationBaseValue(FrameworkElement.MarginProperty) == target) return;

            ViewboxFloatingBar.Margin = target;
            floatingToolbarMarginClock = null;
            if (!canAnimate || current == target)
            {
                CompleteFloatingToolbarMotion(hideToolbar: !showToolbar);
                return;
            }

            var clock = CreateToolbarMarginClock(current, target, 250);
            floatingToolbarMarginClock = clock;
            clock.Completed += (_, _) =>
            {
                if (ReferenceEquals(floatingToolbarMarginClock, clock)) CompleteFloatingToolbarMotion(hideToolbar: !showToolbar);
            };
            ViewboxFloatingBar.ApplyAnimationClock(FrameworkElement.MarginProperty, clock);
        }

        private void CompleteFloatingToolbarMotion(bool hideToolbar)
        {
            floatingToolbarMarginClock = null;
            ViewboxFloatingBar.ApplyAnimationClock(FrameworkElement.MarginProperty, null);
            if (hideToolbar && (!Topmost || isFloatingBarFolded)) ViewboxFloatingBar.Visibility = Visibility.Hidden;
        }

        private async void CursorIcon_Click(object sender, RoutedEventArgs e)
        {
            // 切换前自动截图保存墨迹
            if (inkCanvas.Strokes.Count > 0 && inkCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber)
            {
                if (BtnPPTSlideShowEnd.Visibility == Visibility.Visible) CaptureCurrentPowerPointSlide(requested: false);
                else SaveScreenshot(true);
            }

            if (BtnPPTSlideShowEnd.Visibility != Visibility.Visible)
            {
                if (Settings.Canvas.HideStrokeWhenSelecting)
                    inkCanvas.Visibility = Visibility.Collapsed;
                else
                {
                    inkCanvas.IsHitTestVisible = false;
                    inkCanvas.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (Settings.PowerPointSettings.IsShowStrokeOnSelectInPowerPoint)
                {
                    inkCanvas.Visibility = Visibility.Visible;
                    inkCanvas.IsHitTestVisible = true;
                }
                else
                {
                    if (Settings.Canvas.HideStrokeWhenSelecting)
                        inkCanvas.Visibility = Visibility.Collapsed;
                    else
                    {
                        inkCanvas.IsHitTestVisible = false;
                        inkCanvas.Visibility = Visibility.Visible;
                    }
                }
            }


            Main_Grid.Background = Brushes.Transparent;


            GridBackgroundCoverHolder.Visibility = Visibility.Collapsed;
            inkCanvas.Select(new StrokeCollection());
            GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;

            if (currentMode != 0) SwitchCanvasPage(0);

            CheckEnableTwoFingerGestureBtnVisibility(false);


            StackPanelCanvasControls.Visibility = Visibility.Collapsed;

            if (!isFloatingBarFolded)
            {
                HideSubPanels("cursor", true);
                await Task.Delay(50);
                ViewboxFloatingBarMarginAnimation();
            }
        }

        private void PenIcon_Click(object sender, RoutedEventArgs e)
        {
            if (!IsPenToolActive || StackPanelCanvasControls.Visibility == Visibility.Collapsed)
            {
                if (isFloatingBarFolded)
                {
                    UnFoldFloatingBar_MouseUp(LeftSidePanel, null);
                }
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;

                Main_Grid.Background = new SolidColorBrush(StringToColor("#01FFFFFF"));

                inkCanvas.IsHitTestVisible = true;
                inkCanvas.Visibility = Visibility.Visible;

                GridBackgroundCoverHolder.Visibility = Visibility.Visible;
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;

                StackPanelCanvasControls.Visibility = Visibility.Visible;

                CheckEnableTwoFingerGestureBtnVisibility(true);
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                ColorSwitchCheck();
                HideSubPanels("pen", true);
            }
            else
            {
                ToggleToolbarFlyout(PenPalette, BoardPenPalette, DesktopToolbarPenButton, BoardToolbarPenButton,
                    DesktopPaletteFirstButton, BoardPaletteFirstButton);
            }
        }

        private void ColorThemeSwitch_MouseUp(object sender, RoutedEventArgs e)
        {
            isUselightThemeColor = !isUselightThemeColor;
            if (currentMode == 0)
            {
                isDesktopUselightThemeColor = isUselightThemeColor;
            }
            CheckColorTheme();
        }

        private void EraserIcon_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = true;
            forcePointEraser = true;
            double k = 1;
            switch (Settings.Canvas.EraserSize)
            {
                case 0:
                    k = 0.5;
                    break;
                case 1:
                    k = 0.8;
                    break;
                case 3:
                    k = 1.25;
                    break;
                case 4:
                    k = 1.8;
                    break;
            }
            inkCanvas.EraserShape = new EllipseStylusShape(k * 90, k * 90);
            inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
            drawingShapeMode = ShapeTool.Pen;

            inkCanvas_EditingModeChanged(inkCanvas, null);
            CancelSingleFingerDragMode();

            HideSubPanels("eraser");
        }

        private void EraserIconByStrokes_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = true;
            forcePointEraser = false;

            inkCanvas.EraserShape = new EllipseStylusShape(5, 5);
            inkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            drawingShapeMode = ShapeTool.Pen;

            inkCanvas_EditingModeChanged(inkCanvas, null);
            CancelSingleFingerDragMode();

            HideSubPanels("eraserByStrokes");
        }

        private void CursorWithDelIcon_Click(object sender, RoutedEventArgs e)
        {
            SymbolIconDelete_MouseUp(sender, null);
            CursorIcon_Click(null, null);
        }

        private void SelectIcon_MouseUp(object sender, RoutedEvent e)
        {
            forceEraser = true;
            drawingShapeMode = ShapeTool.Pen;
            inkCanvas.IsManipulationEnabled = false;
            if (inkCanvas.EditingMode == InkCanvasEditingMode.Select)
            {
                StrokeCollection selectedStrokes = new StrokeCollection();
                foreach (Stroke stroke in inkCanvas.Strokes)
                {
                    if (stroke.GetBounds().Width > 0 && stroke.GetBounds().Height > 0)
                    {
                        selectedStrokes.Add(stroke);
                    }
                }
                inkCanvas.Select(selectedStrokes);
            }
            else
            {
                inkCanvas.EditingMode = InkCanvasEditingMode.Select;
            }
        }

        private void CollapseBorderDrawShape(bool isLongPressSelected = false)
        {
            AnimationsHelper.HideWithSlideAndFade(BorderDrawShape);
            AnimationsHelper.HideWithSlideAndFade(BoardBorderDrawShape);
        }

        private void DrawShapePromptToPen()
        {
            if (isLongPressSelected == true)
            {
                HideSubPanels("pen");
            }
            else
            {
                if (StackPanelCanvasControls.Visibility == Visibility.Visible)
                {
                    HideSubPanels("pen");
                }
                else
                {
                    HideSubPanels("cursor");
                }
            }
        }

        private void CloseBordertools_MouseUp(object sender, MouseButtonEventArgs e)
        {
            HideSubPanels();
        }

        #region Left Side Panel

        private void BtnFingerDragMode_Click(object sender, RoutedEventArgs e)
        {
            if (isSingleFingerDragMode)
            {
                isSingleFingerDragMode = false;
                //BtnFingerDragMode.Content = "单指\n拖动";
            }
            else
            {
                isSingleFingerDragMode = true;
                //BtnFingerDragMode.Content = "多指\n拖动";
            }
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (HasNewShapeInProgress && StopNewShape()) return;
            if (inkCanvas.GetSelectedStrokes().Count != 0)
            {
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
                inkCanvas.Select(new StrokeCollection());
            }
            var item = timeMachine.Undo();
            ApplyHistoryToCanvas(item);
        }

        private void BtnRedo_Click(object sender, RoutedEventArgs e)
        {
            if (inkCanvas.GetSelectedStrokes().Count != 0)
            {
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
                inkCanvas.Select(new StrokeCollection());
            }
            var item = timeMachine.Redo();
            ApplyHistoryToCanvas(item);
        }

        private void Element_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!isLoaded) return;
            try
            {
                if (sender is Button button)
                {
                    if (((Button)sender).IsEnabled)
                    {
                        ((UIElement)((Button)sender).Content).Opacity = 1;
                    }
                    else
                    {
                        ((UIElement)((Button)sender).Content).Opacity = 0.5;
                    }
                }
                else if (sender is FontIcon fontIcon)
                {
                    if (((FontIcon)sender).IsEnabled)
                    {
                        ((FontIcon)sender).Opacity = 1;
                    }
                    else
                    {
                        ((FontIcon)sender).Opacity = 0.5;
                    }
                }
            }
            catch { }
        }

        #endregion Left Side Panel

        #region Right Side Panel


        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            try { await settingsWindow.OpenAsync(this); }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
                ShowNotificationAsync(UiText.Get("Actions.OpenSettingsFailed"));
            }
        }

        bool forceEraser = false;


        private async void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (!await TryBackupBeforeClearAsync()) return;
            forceEraser = false;
            //BorderClearInDelete.Visibility = Visibility.Collapsed;

            if (currentMode == 0)
            { // 先回到画笔再清屏，避免 TimeMachine 的相关 bug 影响
                if (!IsPenToolActive && StackPanelCanvasControls.Visibility == Visibility.Visible)
                {
                    PenIcon_Click(null, null);
                }
            }
            else
            {
                if (!IsPenToolActive)
                {
                    PenIcon_Click(null, null);
                }
            }

            ClearStrokes(false);

            CancelSingleFingerDragMode();
        }

        private async Task AutoClearWhenLeavingWritingAsync()
        {
            if (!isLoaded || !Settings.Automation.IsAutoClearWhenExitingWritingMode || !HasCanvasContent) return;
            if (await TryBackupBeforeClearAsync()) ClearStrokes(false);
        }

        bool lastIsInMultiTouchMode = false;

        private void CancelSingleFingerDragMode()
        {
            if (ToggleSwitchDrawShapeBorderAutoHide.IsOn)
            {
                CollapseBorderDrawShape();
            }

            GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;

            if (isSingleFingerDragMode)
            {
                BtnFingerDragMode_Click(null, null);
            }
            isLongPressSelected = false;
        }

        int currentMode = 0;

        private void BtnSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (Main_Grid.Background == Brushes.Transparent)
            {
                if (currentMode == 1)
                {
                    GridBackgroundCover.Visibility = Visibility.Collapsed;
                    AnimationsHelper.HideWithSlideAndFade(BlackboardLeftSide);
                    AnimationsHelper.HideWithSlideAndFade(BlackboardCenterSide);
                    AnimationsHelper.HideWithSlideAndFade(BlackboardRightSide);

                    SwitchCanvasPage(CurrentWhiteboardIndex);
                }
                Topmost = true;
                BtnHideInkCanvas_Click(null, e);
            }
            else
            {
                switch (currentMode)
                {
                    case 0: //屏幕模式
                        currentMode = 0;
                        GridBackgroundCover.Visibility = Visibility.Collapsed;
                        AnimationsHelper.HideWithSlideAndFade(BlackboardLeftSide);
                        AnimationsHelper.HideWithSlideAndFade(BlackboardCenterSide);
                        AnimationsHelper.HideWithSlideAndFade(BlackboardRightSide);

                        SwitchCanvasPage(0);
                        Topmost = true;
                        break;
                    case 1: //黑板或白板模式
                        currentMode = 1;
                        GridBackgroundCover.Visibility = Visibility.Visible;
                        AnimationsHelper.ShowWithSlideFromBottomAndFade(BlackboardLeftSide);
                        AnimationsHelper.ShowWithSlideFromBottomAndFade(BlackboardCenterSide);
                        AnimationsHelper.ShowWithSlideFromBottomAndFade(BlackboardRightSide);

                        SwitchCanvasPage(CurrentWhiteboardIndex);

                        Topmost = false;
                        break;
                }
            }
        }

        int BoundsWidth = 5;

        private void BtnHideInkCanvas_Click(object sender, RoutedEventArgs e)
        {
            if (Main_Grid.Background == Brushes.Transparent)
            {
                Main_Grid.Background = new SolidColorBrush(StringToColor("#01FFFFFF"));
                inkCanvas.IsHitTestVisible = true;
                inkCanvas.Visibility = Visibility.Visible;

                GridBackgroundCoverHolder.Visibility = Visibility.Visible;

                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (BtnPPTSlideShowEnd.Visibility != Visibility.Visible)
                {
                    if (currentMode == 0) _ = AutoClearWhenLeavingWritingAsync();
                    inkCanvas.IsHitTestVisible = true;
                    inkCanvas.Visibility = Visibility.Visible;
                }
                else
                {
                    if (currentMode == 0 && !Settings.PowerPointSettings.IsNoClearStrokeOnSelectWhenInPowerPoint)
                        _ = AutoClearWhenLeavingWritingAsync();


                    if (Settings.PowerPointSettings.IsShowStrokeOnSelectInPowerPoint)
                    {
                        inkCanvas.Visibility = Visibility.Visible;
                        inkCanvas.IsHitTestVisible = true;
                    }
                    else
                    {
                        inkCanvas.IsHitTestVisible = true;
                        inkCanvas.Visibility = Visibility.Visible;
                    }
                }

                Main_Grid.Background = Brushes.Transparent;

                GridBackgroundCoverHolder.Visibility = Visibility.Collapsed;

                if (currentMode != 0) SwitchCanvasPage(0);
            }

            if (Main_Grid.Background == Brushes.Transparent)
            {
                StackPanelCanvasControls.Visibility = Visibility.Collapsed;
                CheckEnableTwoFingerGestureBtnVisibility(false);
                HideSubPanels("cursor");
            }
            else
            {
                AnimationsHelper.ShowWithSlideFromLeftAndFade(StackPanelCanvasControls);
                CheckEnableTwoFingerGestureBtnVisibility(true);
            }
        }
        #endregion
    }
}
