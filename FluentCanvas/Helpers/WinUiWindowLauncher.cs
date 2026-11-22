using System;
using System.Diagnostics;
using FluentCanvas.Localization;

namespace FluentCanvas.Helpers;

internal static class WinUiWindowLauncher
{
    internal const string LanguageEnvironmentVariable = "FLUENTCANVAS_TOOL_UI_LANGUAGE";

    internal static void Open(string action)
    {
        using var child = Start(action);
    }

    internal static Process Start(string action, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException(UiText.Get("Canvas.ProcessPathMissing")))
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.Environment[LanguageEnvironmentVariable] = UiText.ActiveCulture.Name;
        start.ArgumentList.Add(action);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException(UiText.Get("Canvas.ToolStartFailed"));
    }
}
