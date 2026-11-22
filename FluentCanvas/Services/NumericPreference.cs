using System;
using System.Collections.Generic;

namespace FluentCanvas.Services;

internal sealed record NumericPreference(double Minimum, double Maximum, double Step,
    Func<Settings, double> Read, Action<Settings, double> Set, bool WholeNumbers = false)
{
    internal bool Accepts(double value) =>
        value >= Minimum && value <= Maximum && (!WholeNumbers || value == Math.Truncate(value));

    internal static IReadOnlyDictionary<WinUiSetting, NumericPreference> All { get; } =
        new Dictionary<WinUiSetting, NumericPreference>
        {
            [WinUiSetting.ToolbarScale] = new(50, 150, 5,
                s => s.Appearance.FloatingBarScale, (s, value) => s.Appearance.FloatingBarScale = value),
            [WinUiSetting.BlackboardScale] = new(50, 150, 5,
                s => s.Appearance.BlackboardScale, (s, value) => s.Appearance.BlackboardScale = value),
            [WinUiSetting.InkWidth] = new(0.5, 15, 0.5,
                s => s.Canvas.InkWidth, (s, value) => s.Canvas.InkWidth = value),
            [WinUiSetting.SavedFilesRetentionDays] = new(1, 365, 1,
                s => s.Automation.AutoDelSavedFilesDaysThreshold, (s, value) => s.Automation.AutoDelSavedFilesDaysThreshold = (int)value, WholeNumbers: true),
            [WinUiSetting.NibTouchWidth] = new(1, 50, 1,
                s => s.Advanced.NibModeBoundsWidth, (s, value) => s.Advanced.NibModeBoundsWidth = (int)value, WholeNumbers: true),
            [WinUiSetting.FingerTouchWidth] = new(1, 50, 1,
                s => s.Advanced.FingerModeBoundsWidth, (s, value) => s.Advanced.FingerModeBoundsWidth = (int)value, WholeNumbers: true),
            [WinUiSetting.NibEraserThreshold] = new(0, 10, 0.01,
                s => s.Advanced.NibModeBoundsWidthThresholdValue, (s, value) => s.Advanced.NibModeBoundsWidthThresholdValue = value),
            [WinUiSetting.FingerEraserThreshold] = new(0, 10, 0.01,
                s => s.Advanced.FingerModeBoundsWidthThresholdValue, (s, value) => s.Advanced.FingerModeBoundsWidthThresholdValue = value),
            [WinUiSetting.NibEraserScale] = new(0.01, 10, 0.01,
                s => s.Advanced.NibModeBoundsWidthEraserSize, (s, value) => s.Advanced.NibModeBoundsWidthEraserSize = value),
            [WinUiSetting.FingerEraserScale] = new(0.01, 10, 0.01,
                s => s.Advanced.FingerModeBoundsWidthEraserSize, (s, value) => s.Advanced.FingerModeBoundsWidthEraserSize = value),
            [WinUiSetting.TouchMultiplier] = new(0, 5, 0.01,
                s => s.Advanced.TouchMultiplier, (s, value) => s.Advanced.TouchMultiplier = value),
        };
}
