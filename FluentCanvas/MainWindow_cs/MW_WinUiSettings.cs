using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Windows.ApplicationModel;

namespace FluentCanvas
{
    using Services;

    public partial class MainWindow
    {
        internal async Task<WinUiSettingsResponse> ApplyWinUiSettingsAsync(WinUiSettingsRequest request, CancellationToken cancellation)
        {
            string error = null;
            string backupPath = null;
            StartupTaskState? startup = null;
            try
            {
                if (request.Setting == WinUiSetting.Read)
                {
                    startup = await ReadWinUiStartupStateAsync();
                }
                else if (request.Setting == WinUiSetting.ReleaseStoreUpdate)
                {
                    ReleaseStoreUpdate();
                }
                else if (request.Setting == WinUiSetting.PrepareStoreUpdate)
                {
                    (error, backupPath) = await PrepareStoreUpdateAsync(cancellation);
                }
                else if (storeUpdatePrepared || sessionEndPending)
                {
                    error = UiText.Get(storeUpdatePrepared ? "Settings.StoreUpdating" : "Settings.StoreBoardBusy");
                }
                else if (request.Setting == WinUiSetting.GlobalHotkey)
                {
                    error = ApplyGlobalHotkey(request.Id, request.Text);
                }
                else if (request.Setting == WinUiSetting.RunAtStartup && request.Enabled is bool startupEnabled)
                {
                    try { startup = await GetStartupState(startupEnabled); }
                    catch (Exception)
                    {
                        startup = await ReadWinUiStartupStateAsync();
                        error = UiText.Get("Settings.StartupChangeFailed");
                    }
                    if (error == null && startupEnabled && startup is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
                        error = UiText.Get("Settings.StartupNotEnabled");
                }
                else if (request.Setting == WinUiSetting.ErrorReporting && request.Enabled is bool report)
                {
                    if (ErrorReporting.Enabled != report) ErrorReporting.SetEnabled(report);
                }
                else if (request.Setting is (WinUiSetting.Exit or WinUiSetting.Restart) && request.Enabled == true)
                {
                    // The server acknowledges first, then performs the confirmed window action.
                }
                else
                {
                    error = await ApplyWinUiPreferenceAsync(request, cancellation);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                LogHelper.WriteLogToFile($"WinUI settings change failed: {failure}", LogHelper.LogType.Error);
                error = request.Setting == WinUiSetting.PrepareStoreUpdate
                    ? UiText.Get("Settings.StoreBackupFailed") : UiText.Get("Settings.ChangeFailed");
            }

            var state = WinUiSettingsState.Capture(Settings, startup, ErrorReporting.Enabled, ReadGlobalHotkeys());
            return new WinUiSettingsResponse(state, error, backupPath);
        }

        private bool storeUpdatePrepared;

        private async Task<(string Error, string BackupPath)> PrepareStoreUpdateAsync(CancellationToken cancellation)
        {
            if (!StoreUpdateService.IsStoreSigned) return (UiText.Get("Settings.StoreUpdateUnavailable"), null);
            if (activePowerPointSlide != null) return (UiText.Get("Settings.StoreSlidesActive"), null);
            if (storeUpdatePrepared || sessionEndPending || !TryAcquireCanvasBackup())
                return (UiText.Get("Settings.StoreBoardBusy"), null);

            storeUpdatePrepared = true;
            bool ready = false;
            try
            {
                IsEnabled = false;
                var result = await BackUpUnsavedCanvasAsync(beforeStoreUpdate: true, cancellation);
                ready = result.Error == null;
                return result;
            }
            finally
            {
                if (!ready) ReleaseStoreUpdate();
            }
        }

        internal void ReleaseStoreUpdate()
        {
            if (!storeUpdatePrepared) return;
            storeUpdatePrepared = false;
            IsEnabled = true;
            documentOpenGate.Release();
        }

        private static async Task<StartupTaskState?> ReadWinUiStartupStateAsync()
        {
            try { return await GetStartupState(); }
            catch (Exception error)
            {
                LogHelper.WriteLogToFile($"Startup task state unavailable: {error}", LogHelper.LogType.Error);
                return null;
            }
        }

        private async Task<string> ApplyWinUiPreferenceAsync(WinUiSettingsRequest request, CancellationToken cancellation)
        {
            var editor = new SettingsPreferenceEditor(settingsService, SaveSettingsToFile);
            string error = await editor.ApplyAsync(request, cancellation);
            if (error != null) return error;

            try
            {
                ApplyPreferenceToWindow(request.Setting);
                return null;
            }
            catch (Exception failure)
            {
                LogHelper.WriteLogToFile($"Could not apply saved settings: {failure}", LogHelper.LogType.Error);
                return UiText.Get("Settings.SavedApplyFailed");
            }
        }

        private void ApplyPreferenceToWindow(WinUiSetting setting)
        {
            switch (setting)
            {
                case WinUiSetting.Theme:
                    SystemEvents_UserPreferenceChanged(null, null);
                    break;
                case WinUiSetting.ToolbarScale:
                case WinUiSetting.BlackboardScale:
                    ApplyScaling();
                    break;
                case WinUiSetting.InkWidth:
                    ApplyWinUiInkWidth();
                    break;
                case WinUiSetting.NibTouchWidth:
                case WinUiSetting.FingerTouchWidth:
                    ApplyWinUiTouchWidth();
                    break;
                case WinUiSetting.ToolbarLabels:
                    ApplyToolbarLabels();
                    ViewboxFloatingBarMarginAnimation();
                    break;
                case WinUiSetting.Whiteboard:
                    ApplyWinUiBoardBackground();
                    break;
                case WinUiSetting.InkCursor:
                    inkCanvas_EditingModeChanged(inkCanvas, null);
                    break;
                case WinUiSetting.InkRecognition:
                    ApplyInkRecognitionSettings();
                    break;
                case WinUiSetting.MultiTouch:
                case WinUiSetting.AutoSwitchGesture:
                    ApplySceneGestureSettings();
                    break;
                case WinUiSetting.NibMode:
                    ApplyWinUiNibMode();
                    break;
                case WinUiSetting.RotateWithTwoFingers:
                    ApplyWinUiRotation();
                    break;
                case WinUiSetting.PowerPoint:
                case WinUiSetting.Wps:
                    ApplyPresentationSettings();
                    break;
                case WinUiSetting.FoldInSlideShow:
                    StartOrStoptimerCheckAutoFold();
                    break;
                case WinUiSetting.DisableFullscreenEdgeGestures:
                    if (!ApplyEdgeGestureOverride(Settings.Advanced.IsEnableEdgeGestureUtil))
                        throw new InvalidOperationException("Could not update the canvas window edge gesture property.");
                    break;
            }
        }


        private void ApplyWinUiBoardBackground()
        {
            if (Settings.Canvas.UsingWhiteboard && inkColor == 5) lastBoardInkColor = 0;
            if (!Settings.Canvas.UsingWhiteboard && inkColor == 0) lastBoardInkColor = 5;
            SystemEvents_UserPreferenceChanged(null, null);
            CheckColorTheme(true);
            RefreshBoardToolBackgrounds();
        }

        private void ApplyWinUiInkWidth()
        {
            bool wasLoaded = isLoaded;
            isLoaded = false;
            try
            {
                InkWidthSlider.Value = BoardInkWidthSlider.Value = Settings.Canvas.InkWidth * 2;
                double effectiveWidth = inkColor > 100 ? 30 + Settings.Canvas.InkWidth * 2 : Settings.Canvas.InkWidth;
                drawingAttributes.Width = drawingAttributes.Height = effectiveWidth;
            }
            finally { isLoaded = wasLoaded; }
        }

        private void ApplyWinUiTouchWidth() => BoundsWidth = Settings.Startup.IsEnableNibMode
            ? Settings.Advanced.NibModeBoundsWidth : Settings.Advanced.FingerModeBoundsWidth;

        private void ApplyWinUiNibMode()
        {
            bool wasLoaded = isLoaded;
            isLoaded = false;
            try
            {
                ToggleSwitchEnableNibMode.IsOn = BoardToggleSwitchEnableNibMode.IsOn = Settings.Startup.IsEnableNibMode;
                ApplyWinUiTouchWidth();
            }
            finally { isLoaded = wasLoaded; }
        }

        private void ApplyWinUiRotation()
        {
            bool wasLoaded = isLoaded;
            isLoaded = false;
            try
            {
                ToggleSwitchEnableTwoFingerRotation.IsOn =
                    BoardToggleSwitchEnableTwoFingerRotation.IsOn = Settings.Gesture.IsEnableTwoFingerRotation;
            }
            finally { isLoaded = wasLoaded; }
            CheckEnableTwoFingerGestureBtnColorPrompt();
        }

    }
}
