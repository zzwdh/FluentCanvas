using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using FluentCanvas.Services;
using FluentCanvas.Views;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        private readonly SemaphoreSlim documentOpenGate = new(1);
        private bool backingUpForClear;
        private int manualSavesInProgress;

        private async void SymbolIconSaveStrokes_Click(object sender, RoutedEventArgs e)
        {
            if (inkCanvas.Visibility != Visibility.Visible) return;
            RestoreToolbarFlyoutFocus();
            AnimationsHelper.HideWithSlideAndFade(BorderTools);
            AnimationsHelper.HideWithSlideAndFade(BoardBorderTools);
            notifications.Hide();
            await SaveInkCanvasFileAsync(saveByUser: true);
        }

        private async Task<bool> SaveInkCanvasFileAsync(bool newNotice = true, bool saveByUser = false, bool showFailure = true,
            Task screenshotWrite = null)
        {
            if (saveByUser) manualSavesInProgress++;
            try
            {
                CommitPendingCanvasEdits();
                using var snapshot = CanvasDocument.Capture(timeMachine.FreezeInk(), EnumerateCanvasElements());
                int strokeCount = inkCanvas.Strokes.Count;
                var state = timeMachine.CurrentState;
                long context = canvasContextVersion, version = canvasContentVersion;
                string location = Settings.Automation.AutoSavedStrokesLocation;
                bool isWhiteboard = activeCanvasPage != 0;
                string folder = (saveByUser ? "User Saved - " : "Auto Saved - ")
                    + (isWhiteboard ? "BlackBoard Strokes" : "Annotation Strokes");
                string page = isWhiteboard ? $" Page-{activeCanvasPage} StrokesCount-{strokeCount}" : "";
                string name = $"{DateTime.Now:yyyy-MM-dd HH-mm-ss-fff}{page}-{Guid.NewGuid():N}.icart";

                string WriteSnapshot()
                {
                    // A paired PNG occupies the previous slot in the same serial queue.
                    // Its caller reports its own write failure.
                    if (screenshotWrite is { IsCompletedSuccessfully: false }) return null;
                    string directory = Path.Combine(StorageHelper.NormalizeStorageLocation(location), folder);
                    Directory.CreateDirectory(directory);
                    string destination = Path.Combine(directory, name);
                    snapshot.Save(destination);
                    return destination;
                }

                string path = await (screenshotWrite == null ? Task.Run(WriteSnapshot) : screenshotWrites.Enqueue(WriteSnapshot));
                if (path == null) return false;
                if (saveByUser && IsCurrentCanvas(context, version, state)) timeMachine.MarkSaved();
                if (newNotice) ShowNotificationAsync(UiText.Format("Actions.DocumentSaved", path));
                return true;
            }
            catch (Exception ex)
            {
                if (showFailure && !closingPowerPointOwner) ShowNotificationAsync(UiText.Get("Actions.DocumentSaveFailed"));
                LogHelper.WriteLogToFile("墨迹及元素保存失败 | " + ex, LogHelper.LogType.Error);
                return false;
            }
            finally
            {
                if (saveByUser) manualSavesInProgress--;
            }
        }

        private bool ShouldArchiveCanvas => Settings.Automation.IsAutoSaveStrokesAtClear
            && (EnumerateCanvasElements().Any() || EnumerateCommittedStrokes().Count() > Settings.Automation.MinimumAutomationStrokeNumber);

        private bool HasUnsavedCanvasChanges => timeMachine.HasUnsavedChanges || contentGesture != null ||
            strokeManipulationHistory.Count > 0 || ElementsManipulationHistory?.Count > 0 ||
            DrawingAttributesHistory.Count > 0 || AddedStroke?.Count > 0 || ReplacedStroke?.Count > 0;

        private bool IsCurrentCanvas(long context, long version, ulong state) =>
            context == canvasContextVersion && version == canvasContentVersion && state == timeMachine.CurrentState;

        private void ArchiveCanvasForTransition()
        {
            if (!ShouldArchiveCanvas) return;
            _ = SaveInkCanvasFileAsync(newNotice: false);
            SaveScreenshot(true, saveEditable: false);
        }

        private async Task<bool> TryBackupBeforeClearAsync()
        {
            if (backingUpForClear) return false;
            if (!ShouldArchiveCanvas) return true;

            backingUpForClear = true;
            try
            {
                CommitPendingCanvasEdits();
                long context = canvasContextVersion, version = canvasContentVersion;
                ulong state = timeMachine.CurrentState;
                while (true)
                {
                    bool saved = await SaveInkCanvasFileAsync(newNotice: false, showFailure: false);
                    if (!IsCurrentCanvas(context, version, state))
                    {
                        ShowNotificationAsync(UiText.Get("Actions.CanvasChangedDuringBackup"));
                        return false;
                    }
                    if (saved)
                    {
                        SaveScreenshot(true, saveEditable: false);
                        return true;
                    }

                    var choice = ShowDocumentPrompt("Actions.BackupFailed", "Actions.BackupFailedPrompt",
                        "Actions.Retry", "Actions.ClearWithoutBackup");
                    if (!IsCurrentCanvas(context, version, state)) return false;
                    if (choice != MessageBoxResult.Yes) return choice == MessageBoxResult.No;
                }
            }
            finally { backingUpForClear = false; }
        }

        private async void SymbolIconOpenInkCanvasFile_Click(object sender, RoutedEventArgs e)
        {
            RestoreToolbarFlyoutFocus();
            AnimationsHelper.HideWithSlideAndFade(BorderTools);
            AnimationsHelper.HideWithSlideAndFade(BoardBorderTools);

            try
            {
                long context = canvasContextVersion;
                string path = await WinUiPickers.PickOpenFileAsync(new WindowInteropHelper(this).Handle,
                    "FluentCanvas.OpenDocument", new[] { ".icart", ".icstk" }, Settings.Automation.AutoSavedStrokesLocation);
                if (path == null) return;
                if (context == canvasContextVersion) await LoadInkCanvasFileAsync(path);
                else ShowNotificationAsync(UiText.Get("Actions.CanvasChangedWhileOpening"));
            }
            catch (Exception ex)
            {
                ShowNotificationAsync(UiText.Get("Actions.DocumentOpenFailed"));
                LogHelper.WriteLogToFile("选择作品失败 | " + ex, LogHelper.LogType.Error);
            }
        }

        internal async Task LoadInkCanvasFileAsync(string filePath)
        {
            var slideAtRequest = activePowerPointSlide;
            if (readingSlide && (!await slideLoad.WaitAsync() ||
                activePowerPointSlide?.DeckKey != slideAtRequest?.DeckKey ||
                activePowerPointSlide?.SlideId != slideAtRequest?.SlideId ||
                activePowerPointSlide?.SessionId != slideAtRequest?.SessionId))
                return;
            long context = canvasContextVersion;
            await documentOpenGate.WaitAsync();
            try
            {
                if (sessionCloseApproved || closingPowerPointOwner) return;
                if (context != canvasContextVersion)
                {
                    ShowNotificationAsync(UiText.Get("Actions.CanvasChangedWhileOpening"));
                    return;
                }
                using var staged = await Task.Run(() => CanvasDocument.Read(filePath));
                if (context != canvasContextVersion)
                {
                    ShowNotificationAsync(UiText.Get("Actions.CanvasChangedWhileOpening"));
                    return;
                }
                if (HasUnsavedCanvasChanges)
                {
                    var choice = ShowDocumentPrompt("Actions.OpenDocument", "Actions.UnsavedBoardPrompt",
                        "Actions.SaveAndOpen", "Actions.OpenWithoutSaving");
                    if (choice is not (MessageBoxResult.Yes or MessageBoxResult.No) || context != canvasContextVersion) return;
                    if (choice == MessageBoxResult.Yes)
                    {
                        if (!await SaveInkCanvasFileAsync(saveByUser: true)) return;
                        if (context != canvasContextVersion || HasUnsavedCanvasChanges)
                        {
                            ShowNotificationAsync(UiText.Get("Actions.CanvasChangedDuringSave"));
                            return;
                        }
                    }
                }

                ResetCanvasContext();
                var previousReason = _currentCommitType;
                _currentCommitType = CommitReason.CodeInput;
                try { staged.Apply(inkCanvas, timeMachine); }
                finally { _currentCommitType = previousReason; }
                LogHelper.WriteLogToFile($"Opened {Path.GetExtension(filePath).ToLowerInvariant()}: {inkCanvas.Strokes.Count} strokes, {inkCanvas.Children.Count} elements", LogHelper.LogType.Event);
                if (inkCanvas.Visibility != Visibility.Visible) SymbolIconCursor_Click(null, null);
            }
            catch (Exception ex)
            {
                ShowNotificationAsync(UiText.Get("Actions.DocumentOpenFailed"));
                LogHelper.WriteLogToFile("打开作品失败 | " + ex, LogHelper.LogType.Error);
            }
            finally { documentOpenGate.Release(); }
        }

        private MessageBoxResult ShowDocumentPrompt(string title, string message, string accept, string decline)
        {
            var primary = new Forms.TaskDialogButton(UiText.Get(accept));
            var secondary = new Forms.TaskDialogButton(UiText.Get(decline));
            var cancel = new Forms.TaskDialogButton(UiText.Get("Actions.Cancel"));
            var page = new Forms.TaskDialogPage
            {
                Caption = UiText.Get(title),
                Text = UiText.Get(message),
                Icon = Forms.TaskDialogIcon.Warning,
                AllowCancel = true,
                DefaultButton = cancel,
                RightToLeftLayout = UiText.ActiveCulture.TextInfo.IsRightToLeft,
                Buttons = { primary, secondary, cancel },
            };
            var answer = Forms.TaskDialog.ShowDialog(new WindowInteropHelper(this).Handle, page,
                Forms.TaskDialogStartupLocation.CenterOwner);
            if (answer == primary) return MessageBoxResult.Yes;
            return answer == secondary ? MessageBoxResult.No : MessageBoxResult.Cancel;
        }
    }
}
