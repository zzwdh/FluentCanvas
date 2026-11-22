using FluentCanvas.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCanvas.Services;

internal sealed class SettingsPreferenceEditor(ISettingsService profile, Func<bool> save)
{
    internal async Task<string> ApplyAsync(WinUiSettingsRequest request, CancellationToken cancellation)
    {
        if (request.Setting == WinUiSetting.SaveDirectory && !string.IsNullOrWhiteSpace(request.Text))
            return await SetSaveDirectoryAsync(request.Text, cancellation);

        var settings = profile.Model;
        if (NumericPreference.All.TryGetValue(request.Setting, out var preference))
        {
            if (request.Number is not double number || !preference.Accepts(number))
                return UiText.Get("Settings.InvalidValue");

            return Persist(preference.Read(settings), number, value => preference.Set(settings, value));
        }

        switch (request.Setting)
        {
            case WinUiSetting.Language when UiText.IsSupportedPreference(request.Text):
                return Persist(settings.Appearance.Language, request.Text,
                    value => settings.Appearance.Language = value);

            case WinUiSetting.Theme when request.Number is 0 or 1 or 2:
                return Persist(settings.Appearance.Theme, (int)request.Number.Value,
                    value => settings.Appearance.Theme = value);
        }

        if (request.Enabled is not bool enabled) return UiText.Get("Settings.InvalidValue");
        return request.Setting switch
        {
            WinUiSetting.ToolbarLabels => Persist(settings.Appearance.IsEnableDisPlayFloatBarText, enabled,
                value => settings.Appearance.IsEnableDisPlayFloatBarText = value),
            WinUiSetting.Whiteboard => Persist(settings.Canvas.UsingWhiteboard, enabled,
                value => settings.Canvas.UsingWhiteboard = value),
            WinUiSetting.InkCursor => Persist(settings.Canvas.IsShowCursor, enabled,
                value => settings.Canvas.IsShowCursor = value),
            WinUiSetting.InkRecognition => Persist(settings.InkToShape.IsInkToShapeEnabled, enabled,
                value => settings.InkToShape.IsInkToShapeEnabled = value),
            WinUiSetting.MultiTouch => Persist(settings.Gesture.IsEnableMultiTouchMode, enabled,
                value => settings.Gesture.IsEnableMultiTouchMode = value),
            WinUiSetting.AutoSwitchGesture => Persist(settings.Gesture.AutoSwitchTwoFingerGesture, enabled,
                value => settings.Gesture.AutoSwitchTwoFingerGesture = value),
            WinUiSetting.NibMode => Persist(settings.Startup.IsEnableNibMode, enabled,
                value => settings.Startup.IsEnableNibMode = value),
            WinUiSetting.SpecialTouchScreen => Persist(settings.Advanced.IsSpecialScreen, enabled,
                value => settings.Advanced.IsSpecialScreen = value),
            WinUiSetting.QuadInfrared => Persist(settings.Advanced.IsQuadIR, enabled,
                value => settings.Advanced.IsQuadIR = value),
            WinUiSetting.RotateWithTwoFingers => Persist(settings.Gesture.IsEnableTwoFingerRotation, enabled,
                value => settings.Gesture.IsEnableTwoFingerRotation = value),
            WinUiSetting.PowerPoint => Persist(settings.PowerPointSettings.PowerPointSupport, enabled,
                value => settings.PowerPointSettings.PowerPointSupport = value),
            WinUiSetting.Wps => Persist(settings.PowerPointSettings.IsSupportWPS, enabled,
                value => settings.PowerPointSettings.IsSupportWPS = value),
            WinUiSetting.AnnotateOnSlideShow => Persist(settings.PowerPointSettings.IsShowCanvasAtNewSlideShow, enabled,
                value => settings.PowerPointSettings.IsShowCanvasAtNewSlideShow = value),
            WinUiSetting.SaveSlideInk => Persist(settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint, enabled,
                value => settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint = value),
            WinUiSetting.CaptureSlide => Persist(settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint, enabled,
                value => settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint = value),
            WinUiSetting.CaptureOnClear => Persist(settings.Automation.IsAutoSaveStrokesAtClear, enabled,
                value => settings.Automation.IsAutoSaveStrokesAtClear = value),
            WinUiSetting.SaveInkOnCapture => Persist(settings.Automation.IsAutoSaveStrokesAtScreenshot, enabled,
                value => settings.Automation.IsAutoSaveStrokesAtScreenshot = value),
            WinUiSetting.DateFolders => Persist(settings.Automation.IsSaveScreenshotsInDateFolders, enabled,
                value => settings.Automation.IsSaveScreenshotsInDateFolders = value),
            WinUiSetting.AutoCleanSavedFiles => Persist(settings.Automation.AutoDelSavedFiles, enabled,
                value => settings.Automation.AutoDelSavedFiles = value),
            WinUiSetting.FoldAtStartup => Persist(settings.Startup.IsFoldAtStartup, enabled,
                value => settings.Startup.IsFoldAtStartup = value),
            WinUiSetting.FoldInSlideShow => Persist(settings.Automation.IsAutoFoldInPPTSlideShow, enabled,
                value => settings.Automation.IsAutoFoldInPPTSlideShow = value),
            WinUiSetting.DisableFullscreenEdgeGestures => Persist(settings.Advanced.IsEnableEdgeGestureUtil, enabled,
                value => settings.Advanced.IsEnableEdgeGestureUtil = value),
            _ => UiText.Get("Settings.SettingUnavailable")
        };
    }

    private string Persist<T>(T previous, T next, Action<T> set)
    {
        if (EqualityComparer<T>.Default.Equals(previous, next)) return null;

        set(next);
        try
        {
            if (save()) return null;
        }
        catch
        {
            set(previous);
            throw;
        }
        set(previous);
        return UiText.Get("Settings.SaveFailed");
    }

    private async Task<string> SetSaveDirectoryAsync(string path, CancellationToken cancellation)
    {
        string directory;
        try
        {
            directory = await Task.Run(() => ValidateSaveDirectory(path), cancellation)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        }
        catch (TimeoutException)
        {
            return UiText.Get("Settings.DirectoryTimeout");
        }

        if (directory == null) return UiText.Get("Settings.DirectoryNotWritable");
        // A preset may replace the live profile while the directory is being checked.
        var automation = profile.Model.Automation;
        return Persist(automation.AutoSavedStrokesLocation, directory, value => automation.AutoSavedStrokesLocation = value);
    }

    private static string ValidateSaveDirectory(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            string fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath)) return null;
            string probe = Path.Combine(fullPath, $".fluentcanvas-{Guid.NewGuid():N}.tmp");
            using var test = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return fullPath;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }
}
