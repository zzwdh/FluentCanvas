using System;
using System.Threading.Tasks;
using FluentCanvas.Helpers;
using Windows.ApplicationModel;
using Windows.Services.Store;
using Windows.Storage;

namespace FluentCanvas.Services;

internal static class StoreUpdateService
{
    private const string AutoDownloadKey = "StoreAutoDownload";

    internal static bool IsStoreSigned
    {
        get
        {
            if (!App.IsPackaged) return false;
            try { return Package.Current.SignatureKind == PackageSignatureKind.Store; }
            catch (Exception) { return false; }
        }
    }

    internal static bool AutoDownload
    {
        get
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            return !settings.TryGetValue(AutoDownloadKey, out object value) || value is not false;
        }
        set => ApplicationData.Current.LocalSettings.Values[AutoDownloadKey] = value;
    }

    internal static StoreContext CreateContext(nint owner)
    {
        var context = StoreContext.GetDefault();
        if (owner != 0) WinRT.Interop.InitializeWithWindow.Initialize(context, owner);
        return context;
    }

    // Stage Store-signed packages without interrupting the current whiteboard session.
    // Microsoft Store handles automatic installation according to the user's Store policy.
    internal static async Task DownloadAtStartupAsync()
    {
        if (!IsStoreSigned) return;
        try
        {
            if (!AutoDownload) return;
            var context = CreateContext(0);
            if (!context.CanSilentlyDownloadStorePackageUpdates) return;
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates.Count == 0 || !AutoDownload || !context.CanSilentlyDownloadStorePackageUpdates) return;
            var result = await context.TrySilentDownloadStorePackageUpdatesAsync(updates);
            LogHelper.WriteLogToFile($"Microsoft Store background download: {result.OverallState}");
        }
        catch (Exception ex)
        {
            LogHelper.WriteLogToFile($"Microsoft Store background download failed: 0x{ex.HResult:X8}", LogHelper.LogType.Error);
        }
    }
}
