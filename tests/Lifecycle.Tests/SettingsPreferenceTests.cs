using FluentCanvas;
using FluentCanvas.Services;

internal static class SettingsPreferenceTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var settings = new Settings();
        settings.Automation.AutoDelSavedFiles = true;
        settings.Automation.AutoDelSavedFilesDaysThreshold = 21;
        settings.GlobalHotkeys["ClearCanvas"] = "Alt+K";
        var recommended = SettingsPresets.Recommended(settings);
        check(recommended.Automation.AutoDelSavedFiles && recommended.Automation.AutoDelSavedFilesDaysThreshold == 21 &&
            recommended.GlobalHotkeys["ClearCanvas"] == "Alt+K" && recommended.Canvas.InkAlpha == 80,
            "Recommended settings preserve retention and shortcuts while applying the ink defaults");
        recommended.GlobalHotkeys["ClearCanvas"] = "";
        check(settings.GlobalHotkeys["ClearCanvas"] == "Alt+K", "Preparing a profile leaves rollback data independent");
        var special = SettingsPresets.ForSpecialVersion(settings);
        check(special.Automation.AutoDelSavedFiles && special.Automation.AutoDelSavedFilesDaysThreshold == 15 &&
            special.Appearance.IsEnableDisPlayFloatBarText && special.Automation.AutoSavedStrokesLocation == @"D:\FluentCanvas",
            "SpecialVersion profile retains its toolbar, location and retention overrides");

        int writes = 0;
        bool writable = true;
        var profile = new LocalSettingsService { Model = settings };
        var editor = new SettingsPreferenceEditor(profile, () =>
        {
            writes++;
            return writable;
        });
        Task<string> Edit(WinUiSettingsRequest request) => editor.ApplyAsync(request, CancellationToken.None);

        check(await Edit(new(WinUiSetting.InkWidth, Number: 15)) == null &&
            settings.Canvas.InkWidth == 15 && writes == 1, "Ink width commits at the upper boundary");
        check(await Edit(new(WinUiSetting.InkWidth, Number: 15)) == null && writes == 1,
            "Repeating a preference does not rewrite the file");
        check(await Edit(new(WinUiSetting.SavedFilesRetentionDays, Number: 2.5)) != null &&
            await Edit(new(WinUiSetting.InkWidth, Number: double.NaN)) != null &&
            await Edit(new(WinUiSetting.ToolbarScale, Number: 151)) != null && writes == 1,
            "Fractional days, non-finite widths and out-of-range scales are rejected before saving");

        writable = false;
        check(await Edit(new(WinUiSetting.InkWidth, Number: 3)) != null && settings.Canvas.InkWidth == 15,
            "Failed preference persistence restores the live model");
        writable = true;
        check(await Edit(new(WinUiSetting.Wps, Enabled: true)) == null && settings.PowerPointSettings.IsSupportWPS,
            "WPS opt-in reaches the presentation setting");
        profile.Model = special;
        string directory = Directory.CreateDirectory(Path.Combine(root, "saves")).FullName;
        check(await Edit(new(WinUiSetting.SaveDirectory, Text: directory)) == null &&
            special.Automation.AutoSavedStrokesLocation == directory && settings.Automation.AutoSavedStrokesLocation != directory &&
            !Directory.EnumerateFiles(directory, ".fluentcanvas-*.tmp").Any(),
            "Save directory updates the current profile and removes the write probe");
    }
}
