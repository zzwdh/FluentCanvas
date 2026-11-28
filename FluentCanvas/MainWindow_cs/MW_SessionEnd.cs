using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using Microsoft.Windows.AppLifecycle;

namespace FluentCanvas;

public partial class MainWindow
{
    private bool sessionEndPending;
    private bool sessionCloseApproved;

    private bool TryAcquireCanvasBackup() =>
        IsEnabled && manualSavesInProgress == 0 && !backingUpForClear &&
        !inkCanvas.IsMouseCaptureWithin && !inkCanvas.IsStylusCaptureWithin && !inkCanvas.AreAnyTouchesCapturedWithin &&
        documentOpenGate.Wait(0);

    private async Task FinishSessionAsync(bool restart, bool confirmTrayExit = false, bool confirmWindowClose = false)
    {
        if (sessionEndPending || sessionCloseApproved) return;
        if (storeUpdatePrepared || !TryAcquireCanvasBackup())
        {
            string message = storeUpdatePrepared ? "Settings.StoreUpdating" :
                manualSavesInProgress == 0 ? "Settings.StoreBoardBusy" :
                restart ? "Actions.SavingBeforeRestart" : "Actions.SavingBeforeClose";
            ShowNotificationAsync(UiText.Get(message));
            return;
        }

        sessionEndPending = true;
        try
        {
            if (confirmTrayExit)
            {
                ActivateCanvas();
                if (!ConfirmSessionExit("Settings.SaveBeforeExit", "Settings.ExitApp", MessageBoxImage.Warning)) return;
            }
            if (confirmWindowClose &&
                (!ConfirmSessionExit("Settings.SaveBeforeExit", "Settings.ExitApp", MessageBoxImage.Warning) ||
                 !ConfirmSessionExit("Actions.ConfirmClose", "Settings.ExitApp", MessageBoxImage.Error) ||
                  ConfirmSessionExit("Actions.CancelClosing", "Settings.ExitApp", MessageBoxImage.Error)))
                return;

            IsEnabled = false;
            var (error, backupPath) = await BackUpUnsavedCanvasAsync(beforeStoreUpdate: false, CancellationToken.None);
            if (error != null)
            {
                ShowNotificationAsync(error);
                return;
            }
            if (backupPath != null)
                LogHelper.WriteLogToFile($"Session backup saved to {backupPath}", LogHelper.LogType.Event);

            if (restart)
            {
                RestartAfterBackup();
            }
            else
            {
                sessionCloseApproved = true;
                Close();
            }
        }
        catch (Exception error)
        {
            LogHelper.WriteLogToFile($"Session backup failed: {error}", LogHelper.LogType.Error);
            ShowNotificationAsync(UiText.Get("Settings.StoreBackupFailed"));
        }
        finally
        {
            if (!closingPowerPointOwner)
            {
                sessionCloseApproved = false;
                IsEnabled = true;
            }
            sessionEndPending = false;
            documentOpenGate.Release();
        }
    }

    private bool ConfirmSessionExit(string text, string title, MessageBoxImage icon) =>
        MessageBox.Show(this, UiText.Get(text), UiText.Get(title), MessageBoxButton.OKCancel, icon,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private void RestartAfterBackup()
    {
        ClearEdgeGestureOverride();
        try
        {
            var reason = AppInstance.Restart(string.Empty);
            LogHelper.WriteLogToFile($"Restart did not complete: {reason}", LogHelper.LogType.Error);
        }
        catch (Exception ex)
        {
            LogHelper.WriteLogToFile($"Restart failed: {ex}", LogHelper.LogType.Error);
        }
        finally
        {
            ShellSetEdgeGestureUtil(Settings.Advanced.IsEnableEdgeGestureUtil);
        }
        ShowNotificationAsync(UiText.Get("Actions.RestartFailed"));
    }

    private async Task<(string Error, string BackupPath)> BackUpUnsavedCanvasAsync(bool beforeStoreUpdate,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        InvalidateInkRecognition();
        CommitPendingCanvasEdits();
        var presentation = activePowerPointSlide;
        if (beforeStoreUpdate && presentation != null)
            return (UiText.Get("Settings.StoreSlidesActive"), null);

        // Queue the slide currently on screen as well as the previous slides already in the writer.
        if (presentation != null)
        {
            SaveActivePowerPointSlide();
            if (presentation.CanPersistInk &&
                (Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint || Settings.PowerPointSettings.IsNotifyPreviousPage))
                powerPointWriter.SaveLastSlide(powerPointInk, presentation.DeckKey, presentation.SlideId);
        }

        long context = canvasContextVersion, version = canvasContentVersion;
        ulong state = timeMachine.CurrentState;
        if (!await powerPointWriter.FlushAsync())
            return (UiText.Get("Settings.StoreSlidesUnsaved"), null);
        if (!beforeStoreUpdate) await screenshotWrites.WhenIdle();
        cancellation.ThrowIfCancellationRequested();
        if (!IsCurrentCanvas(context, version, state) || activePowerPointSlide != presentation)
            return (UiText.Get("Settings.StoreBackupStateChanged"), null);

        using var pages = CanvasSessionBackup.Capture(Pages, HasUnsavedCanvasChanges,
            presentation != null ? desktopBeforePowerPoint : null, powerPointPages);
        if (pages.Count == 0) return (null, null);
        string location = Settings.Automation.AutoSavedStrokesLocation;
        string category = beforeStoreUpdate ? "更新前备份" : "Session Backups";
        string path = await Task.Run(() => WriteCanvasBackups(location, category, pages, cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return IsCurrentCanvas(context, version, state) && activePowerPointSlide == presentation
            ? (null, path)
            : (UiText.Get("Settings.StoreBackupStateChanged"), path);
    }


    private static string WriteCanvasBackups(string location, string category,
        IReadOnlyList<(string Name, CanvasDocument.Snapshot Document)> pages, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string directory = Path.Combine(StorageHelper.NormalizeStorageLocation(location), category,
            $"{DateTime.Now:yyyy-MM-dd HH-mm-ss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        foreach (var (name, document) in pages)
        {
            cancellation.ThrowIfCancellationRequested();
            document.Save(Path.Combine(directory, name));
        }
        return directory;
    }
}
