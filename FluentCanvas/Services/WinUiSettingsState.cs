#nullable enable

using Windows.ApplicationModel;

namespace FluentCanvas.Services;

internal sealed record WinUiSettingsState(
    int Theme, double ToolbarScale, bool ToolbarLabels, bool Whiteboard, double InkWidth,
    bool InkCursor, bool InkRecognition, bool MultiTouch, bool RotateWithTwoFingers,
    bool PowerPoint, bool Wps, bool AnnotateOnSlideShow, bool SaveSlideInk, bool CaptureSlide,
    bool CaptureOnClear, bool SaveInkOnCapture, bool DateFolders, string SaveDirectory,
    bool FoldAtStartup, StartupTaskState? Startup, bool ErrorReporting, bool FoldInSlideShow,
    double BlackboardScale, bool AutoCleanSavedFiles, int SavedFilesRetentionDays, bool AutoSwitchGesture,
    bool NibMode, int NibTouchWidth, int FingerTouchWidth, double NibEraserThreshold, double FingerEraserThreshold,
    double NibEraserScale, double FingerEraserScale, bool SpecialTouchScreen, double TouchMultiplier, bool QuadInfrared,
    GlobalHotkeySetting[] GlobalHotkeys, string Language, bool DisableFullscreenEdgeGestures)
{
    internal static WinUiSettingsState Capture(Settings settings, StartupTaskState? startup,
        bool errorReporting, GlobalHotkeySetting[] hotkeys) => new(
        Theme: settings.Appearance.Theme,
        ToolbarScale: settings.Appearance.FloatingBarScale,
        ToolbarLabels: settings.Appearance.IsEnableDisPlayFloatBarText,
        Whiteboard: settings.Canvas.UsingWhiteboard,
        InkWidth: settings.Canvas.InkWidth,
        InkCursor: settings.Canvas.IsShowCursor,
        InkRecognition: settings.InkToShape.IsInkToShapeEnabled,
        MultiTouch: settings.Gesture.IsEnableMultiTouchMode,
        RotateWithTwoFingers: settings.Gesture.IsEnableTwoFingerRotation,
        PowerPoint: settings.PowerPointSettings.PowerPointSupport,
        Wps: settings.PowerPointSettings.IsSupportWPS,
        AnnotateOnSlideShow: settings.PowerPointSettings.IsShowCanvasAtNewSlideShow,
        SaveSlideInk: settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint,
        CaptureSlide: settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint,
        CaptureOnClear: settings.Automation.IsAutoSaveStrokesAtClear,
        SaveInkOnCapture: settings.Automation.IsAutoSaveStrokesAtScreenshot,
        DateFolders: settings.Automation.IsSaveScreenshotsInDateFolders,
        SaveDirectory: settings.Automation.AutoSavedStrokesLocation,
        FoldAtStartup: settings.Startup.IsFoldAtStartup,
        Startup: startup,
        ErrorReporting: errorReporting,
        FoldInSlideShow: settings.Automation.IsAutoFoldInPPTSlideShow,
        BlackboardScale: settings.Appearance.BlackboardScale,
        AutoCleanSavedFiles: settings.Automation.AutoDelSavedFiles,
        SavedFilesRetentionDays: settings.Automation.AutoDelSavedFilesDaysThreshold,
        AutoSwitchGesture: settings.Gesture.AutoSwitchTwoFingerGesture,
        NibMode: settings.Startup.IsEnableNibMode,
        NibTouchWidth: settings.Advanced.NibModeBoundsWidth,
        FingerTouchWidth: settings.Advanced.FingerModeBoundsWidth,
        NibEraserThreshold: settings.Advanced.NibModeBoundsWidthThresholdValue,
        FingerEraserThreshold: settings.Advanced.FingerModeBoundsWidthThresholdValue,
        NibEraserScale: settings.Advanced.NibModeBoundsWidthEraserSize,
        FingerEraserScale: settings.Advanced.FingerModeBoundsWidthEraserSize,
        SpecialTouchScreen: settings.Advanced.IsSpecialScreen,
        TouchMultiplier: settings.Advanced.TouchMultiplier,
        QuadInfrared: settings.Advanced.IsQuadIR,
        GlobalHotkeys: hotkeys,
        Language: settings.Appearance.Language,
        DisableFullscreenEdgeGestures: settings.Advanced.IsEnableEdgeGestureUtil);
}

internal sealed record WinUiSettingsResponse(WinUiSettingsState State, string? Error = null, string? BackupPath = null);
