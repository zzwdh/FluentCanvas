using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FluentCanvas.Helpers;

internal static class WinUiPickers
{
    private static readonly HashSet<nint> activeOwners = new();

    internal static Task<string> PickOpenFileAsync(nint owner, string settingsIdentifier, IEnumerable<string> extensions,
        string initialDirectory = null)
    {
        return PickAsync(owner, async windowId =>
        {
            var picker = new FileOpenPicker(windowId) { SettingsIdentifier = settingsIdentifier };
            if (Directory.Exists(initialDirectory))
                picker.SuggestedStartFolder = initialDirectory;
            foreach (var extension in extensions)
                picker.FileTypeFilter.Add(extension);
            return (await picker.PickSingleFileAsync())?.Path;
        });
    }

    internal static Task<string> PickSaveFileAsync(nint owner, string settingsIdentifier, string suggestedName,
        string typeLabel, string extension)
    {
        return PickAsync(owner, async windowId =>
        {
            var picker = new FileSavePicker(windowId)
            {
                SuggestedFileName = suggestedName,
                DefaultFileExtension = extension,
                SettingsIdentifier = settingsIdentifier
            };
            picker.FileTypeChoices.Add(typeLabel, new List<string> { extension });
            return (await picker.PickSaveFileAsync())?.Path;
        });
    }

    internal static Task<string> PickSaveDirectoryAsync(nint owner, string initialDirectory)
    {
        return PickAsync(owner, async windowId =>
        {
            var picker = new FolderPicker(windowId) { SettingsIdentifier = "FluentCanvas.SaveDirectory" };
            if (Directory.Exists(initialDirectory))
                picker.SuggestedFolder = initialDirectory;
            return (await picker.PickSingleFolderAsync())?.Path;
        });
    }

    private static async Task<string> PickAsync(nint owner, Func<WindowId, Task<string>> show)
    {
        if (owner == 0)
            throw new ArgumentException("The picker requires an open owner window.", nameof(owner));

        lock (activeOwners)
        {
            if (!activeOwners.Add(owner))
                return null;
        }

        try
        {
            // The picker owns modality. End any ink or selection capture before it opens.
            if (HwndSource.FromHwnd(owner)?.RootVisual is Window window)
                ReleaseInputCapture(window);
            return await show(Win32Interop.GetWindowIdFromWindow(owner));
        }
        finally
        {
            lock (activeOwners)
                activeOwners.Remove(owner);
        }
    }

    private static void ReleaseInputCapture(Window window)
    {
        if (Mouse.Captured is DependencyObject mouse && Window.GetWindow(mouse) == window)
            Mouse.Capture(null);
        if (Stylus.Captured is DependencyObject stylus && Window.GetWindow(stylus) == window)
            Stylus.Capture(null);
        foreach (var touch in window.TouchesCapturedWithin.ToArray())
            touch.Capture(null);
    }
}
