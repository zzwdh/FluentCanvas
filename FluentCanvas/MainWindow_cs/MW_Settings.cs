using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using FluentCanvas.Services;
using FluentCanvas.ViewModels;
using iNKORE.UI.WPF.Modern.Controls;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using File = System.IO.File;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {

        private void ToggleSwitchEnableNibMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            if (sender == ToggleSwitchEnableNibMode)
            {
                BoardToggleSwitchEnableNibMode.IsOn = ToggleSwitchEnableNibMode.IsOn;
            }
            else
            {
                ToggleSwitchEnableNibMode.IsOn = BoardToggleSwitchEnableNibMode.IsOn;
            }
            Settings.Startup.IsEnableNibMode = ToggleSwitchEnableNibMode.IsOn;

            if (Settings.Startup.IsEnableNibMode)
            {
                BoundsWidth = Settings.Advanced.NibModeBoundsWidth;
            }
            else
            {
                BoundsWidth = Settings.Advanced.FingerModeBoundsWidth;
            }
            SaveSettingsToFile();
        }

        private void ApplyScaling()
        {
            // Apply Floating Bar Scale
            // Divide by 100 to convert percentage to scale factor
            double floatingBarScaleFactor = Settings.Appearance.FloatingBarScale / 100.0;
            ViewboxFloatingBarScaleTransform.ScaleX = floatingBarScaleFactor;
            ViewboxFloatingBarScaleTransform.ScaleY = floatingBarScaleFactor;

            // Apply Blackboard Scale
            // Divide by 100 to convert percentage to scale factor
            double blackboardScaleFactor = Settings.Appearance.BlackboardScale / 100.0;
            ViewboxBlackboardLeftSideScaleTransform.ScaleX = blackboardScaleFactor;
            ViewboxBlackboardLeftSideScaleTransform.ScaleY = blackboardScaleFactor;
            ViewboxBlackboardCenterSideScaleTransform.ScaleX = blackboardScaleFactor;
            ViewboxBlackboardCenterSideScaleTransform.ScaleY = blackboardScaleFactor;
            ViewboxBlackboardRightSideScaleTransform.ScaleX = blackboardScaleFactor;
            ViewboxBlackboardRightSideScaleTransform.ScaleY = blackboardScaleFactor;

            // auto align
            ViewboxFloatingBarMarginAnimation();
        }

        private void ComboBoxPenStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            if (sender == ComboBoxPenStyle)
            {
                Settings.Canvas.InkStyle = ComboBoxPenStyle.SelectedIndex;
                BoardComboBoxPenStyle.SelectedIndex = ComboBoxPenStyle.SelectedIndex;
            }
            else
            {
                Settings.Canvas.InkStyle = BoardComboBoxPenStyle.SelectedIndex;
                ComboBoxPenStyle.SelectedIndex = BoardComboBoxPenStyle.SelectedIndex;
            }
            SaveSettingsToFile();
        }

        private void ToggleSwitchEnableTwoFingerZoom_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            if (sender == ToggleSwitchEnableTwoFingerZoom)
            {
                BoardToggleSwitchEnableTwoFingerZoom.IsOn = ToggleSwitchEnableTwoFingerZoom.IsOn;
            }
            else
            {
                ToggleSwitchEnableTwoFingerZoom.IsOn = BoardToggleSwitchEnableTwoFingerZoom.IsOn;
            }
            Settings.Gesture.IsEnableTwoFingerZoom = ToggleSwitchEnableTwoFingerZoom.IsOn;
            CheckEnableTwoFingerGestureBtnColorPrompt();
            SaveSettingsToFile();
        }

        private bool synchronizingGestureControls;
        private bool IsTwoFingerTranslateActive { get; set; }
        private bool IsTwoFingerTranslateOrRotationActive => IsTwoFingerTranslateActive || Settings.Gesture.IsEnableTwoFingerRotation;
        private bool IsTwoFingerGestureActive => Settings.Gesture.IsEnableTwoFingerZoom || IsTwoFingerTranslateOrRotationActive;

        private void ApplySceneGestureSettings()
        {
            var gesture = Settings.Gesture;
            bool desktop = currentMode == 0;
            SetActiveGestureModes(
                gesture.AutoSwitchTwoFingerGesture ? desktop : gesture.IsEnableMultiTouchMode,
                gesture.AutoSwitchTwoFingerGesture ? !desktop : gesture.IsEnableTwoFingerTranslate);
        }

        private void SetActiveGestureModes(bool multiTouch, bool translate)
        {
            synchronizingGestureControls = true;
            try
            {
                if (isInMultiTouchMode != multiTouch) BorderMultiTouchMode_MouseUp(null, null);
                IsTwoFingerTranslateActive = translate;
                ToggleSwitchEnableMultiTouchMode.IsOn = BoardToggleSwitchEnableMultiTouchMode.IsOn = multiTouch;
                ToggleSwitchEnableTwoFingerTranslate.IsOn = BoardToggleSwitchEnableTwoFingerTranslate.IsOn = translate;
            }
            finally { synchronizingGestureControls = false; }
            CheckEnableTwoFingerGestureBtnColorPrompt();
        }

        private void ToggleSwitchEnableMultiTouchMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded || synchronizingGestureControls) return;
            bool enabled = sender == ToggleSwitchEnableMultiTouchMode
                ? ToggleSwitchEnableMultiTouchMode.IsOn : BoardToggleSwitchEnableMultiTouchMode.IsOn;
            SetActiveGestureModes(enabled, IsTwoFingerTranslateActive);
            if (Settings.Gesture.AutoSwitchTwoFingerGesture) return;

            Settings.Gesture.IsEnableMultiTouchMode = enabled;
            SaveSettingsToFile();
        }

        private void ToggleSwitchEnableTwoFingerTranslate_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded || synchronizingGestureControls) return;
            bool enabled = sender == ToggleSwitchEnableTwoFingerTranslate
                ? ToggleSwitchEnableTwoFingerTranslate.IsOn : BoardToggleSwitchEnableTwoFingerTranslate.IsOn;
            SetActiveGestureModes(isInMultiTouchMode, enabled);
            if (Settings.Gesture.AutoSwitchTwoFingerGesture) return;

            Settings.Gesture.IsEnableTwoFingerTranslate = enabled;
            SaveSettingsToFile();
        }

        private void ToggleSwitchEnableTwoFingerRotation_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;

            if (sender == ToggleSwitchEnableTwoFingerRotation)
            {
                BoardToggleSwitchEnableTwoFingerRotation.IsOn = ToggleSwitchEnableTwoFingerRotation.IsOn;
            }
            else
            {
                ToggleSwitchEnableTwoFingerRotation.IsOn = BoardToggleSwitchEnableTwoFingerRotation.IsOn;
            }
            Settings.Gesture.IsEnableTwoFingerRotation = ToggleSwitchEnableTwoFingerRotation.IsOn;
            CheckEnableTwoFingerGestureBtnColorPrompt();
            SaveSettingsToFile();
        }

        private void ApplyToolbarLabels()
        {
            bool shown = Settings.Appearance.IsEnableDisPlayFloatBarText;
            int height = shown ? 22 : 32;
            FloatBarSelectIconTextBlock.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            Icon_Pen.Height = Icon_Eraser1.Height = Icon_Eraser2.Height = height;
            Icon_EraserByStrokes1.Height = Icon_EraserByStrokes2.Height = height;
            Icon_Select1.Height = Icon_Select2.Height = height;
            Icon_Eraser2.Margin = shown ? new Thickness(5, -22, 0, -8) : new Thickness(5, -32, 0, -8);
            Icon_EraserByStrokes2.Margin = shown ? new Thickness(12, -22, 0, -8) : new Thickness(12, -32, 0, -8);
            Icon_Select2.Margin = shown ? new Thickness(6, -18, 0, -8) : new Thickness(6, -28, 0, -8);
            Icon_Undo.Margin = Icon_Redo.Margin = shown ? new Thickness(0, 1.5, 0, -1.5) : new Thickness(0);
        }

        private void ToggleSwitchEnableInkToShape_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded || sender is not ToggleSwitch toggle) return;
            bool previous = Settings.InkToShape.IsInkToShapeEnabled;
            Settings.InkToShape.IsInkToShapeEnabled = toggle.IsOn;
            if (!SaveSettingsToFile()) Settings.InkToShape.IsInkToShapeEnabled = previous;
            ApplyInkRecognitionSettings();
        }

        private void ApplyInkRecognitionSettings()
        {
            bool wasLoaded = isLoaded;
            isLoaded = false;
            try
            {
                bool enabled = Settings.InkToShape.IsInkToShapeEnabled;
                DesktopToggleInkRecognition.IsOn = BoardToggleInkRecognition.IsOn = enabled;
                if (!enabled) InvalidateInkRecognition();
            }
            finally { isLoaded = wasLoaded; }
        }

        private async Task ApplySpecialVersionSettingsAsync()
        {
            await Task.Delay(1000);
            if (Dispatcher.HasShutdownStarted) return;
            bool wasLoaded = isLoaded;
            var previous = Settings;
            isLoaded = false;
            try
            {
                Settings = SettingsPresets.ForSpecialVersion(previous);
                if (!SaveSettingsToFile())
                {
                    Settings = previous;
                    return;
                }
                LoadSettings();
                ShowNotificationAsync("已保存推荐设置。");
            }
            catch (Exception ex) { LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error); }
            finally { isLoaded = wasLoaded; }
        }

        private bool SaveSettingsToFile()
        {
            try
            {
                settingsService.Save();
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"Settings save failed: {ex}", LogHelper.LogType.Error);
                ShowNotificationAsync(UiText.Get("Canvas.SettingsSaveFailed"), true);
                return false;
            }
        }
    }
}
