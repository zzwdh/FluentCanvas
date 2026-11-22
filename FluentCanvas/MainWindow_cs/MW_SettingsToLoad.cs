using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using System;
using System.Windows;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        private void LoadSettings(bool isStartup = false)
        {
            try
            {
                if (!settingsService.Load())
                {
                    Settings = SettingsPresets.Recommended(Settings);
                    SaveSettingsToFile();
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
                ShowNotificationAsync(UiText.Get("Canvas.SettingsReadFailed"));
            }

            // Startup (shell)
            if (isStartup)
            {
                CursorIcon_Click(null, null);

                if (Settings.Automation.AutoDelSavedFiles)
                {
                    DelAutoSavedFiles.DeleteFilesOlder(Settings.Automation.AutoSavedStrokesLocation, Settings.Automation.AutoDelSavedFilesDaysThreshold);
                }
                if (Settings.Startup.IsFoldAtStartup)
                {
                    FoldFloatingBar_Click(Fold_Icon, null);
                }
            }
            if (Settings.Startup.IsEnableNibMode)
            {
                ToggleSwitchEnableNibMode.IsOn = true;
                BoardToggleSwitchEnableNibMode.IsOn = true;
                BoundsWidth = Settings.Advanced.NibModeBoundsWidth;
            }
            else
            {
                ToggleSwitchEnableNibMode.IsOn = false;
                BoardToggleSwitchEnableNibMode.IsOn = false;
                BoundsWidth = Settings.Advanced.FingerModeBoundsWidth;
            }
            if (isStartup)
            {
                _ = Services.StoreUpdateService.DownloadAtStartupAsync();
            }

            // Appearance (shell)
            ApplyToolbarLabels();
            if (Settings.Appearance.IsEnableDisPlayNibModeToggler)
            {
                NibModeSimpleStackPanel.Visibility = Visibility.Visible;
                BoardNibModeSimpleStackPanel.Visibility = Visibility.Visible;
            }
            else
            {
                NibModeSimpleStackPanel.Visibility = Visibility.Collapsed;
                BoardNibModeSimpleStackPanel.Visibility = Visibility.Collapsed;
            }

            SystemEvents_UserPreferenceChanged(null, null);

            ApplyScaling();

            // PowerPointSettings (shell)
            navigation.IsBottomButtonVisible = Settings.PowerPointSettings.IsShowPPTNavigationBottom;
            navigation.IsSideButtonVisible = Settings.PowerPointSettings.IsShowPPTNavigationSides;
            ApplyPresentationSettings();

            // Gesture (shell)
            ToggleSwitchEnableTwoFingerZoom.IsOn = Settings.Gesture.IsEnableTwoFingerZoom;
            BoardToggleSwitchEnableTwoFingerZoom.IsOn = Settings.Gesture.IsEnableTwoFingerZoom;
            ToggleSwitchEnableTwoFingerRotation.IsOn = Settings.Gesture.IsEnableTwoFingerRotation;
            BoardToggleSwitchEnableTwoFingerRotation.IsOn = Settings.Gesture.IsEnableTwoFingerRotation;
            ApplySceneGestureSettings();

            // Canvas (shell)
            drawingAttributes.Height = Settings.Canvas.InkWidth;
            drawingAttributes.Width = Settings.Canvas.InkWidth;

            InkWidthSlider.Value = Settings.Canvas.InkWidth * 2;
            BoardInkWidthSlider.Value = Settings.Canvas.InkWidth * 2;
            InkAlphaSlider.Value = Settings.Canvas.InkAlpha;
            BoardInkAlphaSlider.Value = Settings.Canvas.InkAlpha;

            lastBoardInkColor = Settings.Canvas.UsingWhiteboard ? 0 : 5;

            inkCanvas.ForceCursor = Settings.Canvas.IsShowCursor;

            ComboBoxPenStyle.SelectedIndex = Settings.Canvas.InkStyle;
            BoardComboBoxPenStyle.SelectedIndex = Settings.Canvas.InkStyle;
            ApplyInkRecognitionSettings();

            // Advanced (shell)
            ShellSetEdgeGestureUtil(Settings.Advanced.IsEnableEdgeGestureUtil);

            // Automation (shell)
            StartOrStoptimerCheckAutoFold();

            ViewboxFloatingBarMarginAnimation();
        }
    }
}
