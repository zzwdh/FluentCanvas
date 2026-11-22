namespace FluentCanvas;

internal static class SettingsPresets
{
    internal static Settings Recommended(Settings previous) => new()
    {
        GlobalHotkeys = previous.GlobalHotkeys is { } hotkeys ? new(hotkeys, hotkeys.Comparer) : new(),
        Advanced =
        {
            IsSpecialScreen = true,
            IsQuadIR = false,
            TouchMultiplier = 0.3,
            NibModeBoundsWidth = 5,
            FingerModeBoundsWidth = 20,
            NibModeBoundsWidthThresholdValue = 2.5,
            FingerModeBoundsWidthThresholdValue = 2.5,
            NibModeBoundsWidthEraserSize = 0.8,
            FingerModeBoundsWidthEraserSize = 0.8,
            IsLogEnabled = true,
            IsSecondConfimeWhenShutdownApp = false,
            IsEnableEdgeGestureUtil = false,
        },
        Appearance =
        {
            IsEnableDisPlayFloatBarText = false,
            IsEnableDisPlayNibModeToggler = false,
            IsColorfulViewboxFloatingBar = false,
            FloatingBarScale = 100.0,
            BlackboardScale = 100.0,
            IsTransparentButtonBackground = true,
            IsShowExitButton = true,
            IsShowEraserButton = true,
            IsShowHideControlButton = false,
            IsShowLRSwitchButton = false,
            IsShowModeFingerToggleSwitch = true,
            Theme = 0,
        },
        Automation =
        {
            IsAutoFoldInEasiNote = true,
            IsAutoFoldInEasiNoteIgnoreDesktopAnno = true,
            IsAutoFoldInEasiCamera = true,
            IsAutoFoldInEasiNote3C = false,
            IsAutoFoldInSeewoPincoTeacher = false,
            IsAutoFoldInHiteTouchPro = false,
            IsAutoFoldInHiteCamera = false,
            IsAutoFoldInWxBoardMain = false,
            IsAutoFoldInOldZyBoard = false,
            IsAutoFoldInMSWhiteboard = false,
            IsAutoFoldInPPTSlideShow = false,
            IsSaveScreenshotsInDateFolders = false,
            IsAutoSaveStrokesAtScreenshot = true,
            IsAutoSaveStrokesAtClear = true,
            IsAutoClearWhenExitingWritingMode = false,
            MinimumAutomationStrokeNumber = 0,
            AutoDelSavedFiles = previous.Automation.AutoDelSavedFiles,
            AutoDelSavedFilesDaysThreshold = previous.Automation.AutoDelSavedFilesDaysThreshold,
        },
        PowerPointSettings =
        {
            IsShowPPTNavigationBottom = false,
            IsShowBottomPPTNavigationPanel = true,
            IsShowSidePPTNavigationPanel = true,
            PowerPointSupport = true,
            IsShowCanvasAtNewSlideShow = true,
            IsNoClearStrokeOnSelectWhenInPowerPoint = true,
            IsShowStrokeOnSelectInPowerPoint = false,
            IsAutoSaveStrokesInPowerPoint = true,
            IsAutoSaveScreenShotInPowerPoint = true,
            IsNotifyPreviousPage = false,
            IsNotifyHiddenPage = false,
            IsNotifyAutoPlayPresentation = true,
            IsEnableTwoFingerGestureInPresentationMode = false,
            IsEnableFingerGestureSlideShowControl = false,
            IsSupportWPS = false,
        },
        Canvas =
        {
            InkWidth = 2.5,
            InkAlpha = 80,
            IsShowCursor = false,
            InkStyle = 0,
            EraserSize = 1,
            EraserType = 0,
            HideStrokeWhenSelecting = false,
            UsingWhiteboard = false,
            HyperbolaAsymptoteOption = 0,
        },
        Gesture =
        {
            MatrixTransformCenterPoint = MatrixTransformCenterPointOptions.CanvasCenterPoint,
            AutoSwitchTwoFingerGesture = true,
            IsEnableTwoFingerTranslate = true,
            IsEnableTwoFingerZoom = false,
            IsEnableTwoFingerRotation = false,
            IsEnableTwoFingerRotationOnSelection = true,
        },
        InkToShape =
        {
            IsInkToShapeEnabled = true,
        },
        Startup =
        {
            IsEnableNibMode = false,
            IsAutoUpdate = true,
            IsAutoUpdateWithSilence = true,
            AutoUpdateWithSilenceStartTime = "18:20",
            AutoUpdateWithSilenceEndTime = "07:40",
            IsFoldAtStartup = false,
        },
    };

    internal static Settings ForSpecialVersion(Settings previous)
    {
        var settings = Recommended(previous);
        settings.Automation.AutoDelSavedFiles = true;
        settings.Automation.AutoDelSavedFilesDaysThreshold = 15;
        settings.Appearance.IsEnableDisPlayFloatBarText = true;
        settings.Automation.AutoSavedStrokesLocation = @"D:\FluentCanvas";
        return settings;
    }
}
