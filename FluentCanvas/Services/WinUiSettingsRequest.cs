#nullable enable

namespace FluentCanvas.Services;

internal enum WinUiSetting
{
    Read, Theme, ToolbarScale, ToolbarLabels, Whiteboard, InkWidth, InkCursor, InkRecognition,
    MultiTouch, RotateWithTwoFingers, PowerPoint, Wps, AnnotateOnSlideShow, SaveSlideInk,
    CaptureSlide, CaptureOnClear, SaveInkOnCapture, DateFolders, SaveDirectory,
    FoldAtStartup, RunAtStartup, ErrorReporting, FoldInSlideShow, Restart, Exit,
    BlackboardScale, AutoCleanSavedFiles, SavedFilesRetentionDays, AutoSwitchGesture, NibMode,
    NibTouchWidth, FingerTouchWidth, NibEraserThreshold, FingerEraserThreshold,
    NibEraserScale, FingerEraserScale, SpecialTouchScreen, TouchMultiplier, QuadInfrared,
    PrepareStoreUpdate, ReleaseStoreUpdate, GlobalHotkey, Language, DisableFullscreenEdgeGestures
}

internal sealed record WinUiSettingsRequest(
    WinUiSetting Setting, bool? Enabled = null, double? Number = null, string? Text = null, string? Id = null);
