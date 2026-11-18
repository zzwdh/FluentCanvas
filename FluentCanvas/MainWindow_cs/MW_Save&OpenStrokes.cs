using FluentCanvas.Helpers;
using FluentCanvas.Services;
using FluentCanvas.Views;
using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        private void SymbolIconSaveStrokes_Click(object sender, RoutedEventArgs e)
        {
            if (inkCanvas.Visibility != Visibility.Visible) return;
            AnimationsHelper.HideWithSlideAndFade(BorderTools);
            AnimationsHelper.HideWithSlideAndFade(BoardBorderTools);
            if (Locator.Get<IAppViewService>().TryGet<INotificationsView>(out var notificationsView))
            {
                notificationsView.HideView();
            }
            SaveInkCanvasFile(true, true);
        }

        private void SaveInkCanvasFile(bool newNotice = true, bool saveByUser = false)
        {
            try
            {
                var basePath = StorageHelper.NormalizeStorageLocation(Settings.Automation.AutoSavedStrokesLocation);
                Settings.Automation.AutoSavedStrokesLocation = basePath;

                string savePath = basePath
                    + (saveByUser ? @"\User Saved - " : @"\Auto Saved - ")
                    + (currentMode == 0 ? "Annotation Strokes" : "BlackBoard Strokes");

                StorageHelper.EnsureDirectory(savePath);

                string savePathWithName = savePath + @"\" + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff")
                    + (currentMode != 0 ? " Page-" + CurrentWhiteboardIndex + " StrokesCount-" + inkCanvas.Strokes.Count + ".icart" : ".icart");

                CanvasDocument.Save(savePathWithName, inkCanvas);
                if (newNotice) ShowNotificationAsync("墨迹及元素成功保存至 " + savePathWithName);
            }
            catch (Exception Ex)
            {
                ShowNotificationAsync("墨迹及元素保存失败！");
                LogHelper.WriteLogToFile("墨迹及元素保存失败 | " + Ex.ToString(), LogHelper.LogType.Error);
            }
        }

        private void SymbolIconOpenInkCanvasFile_Click(object sender, RoutedEventArgs e)
        {
            AnimationsHelper.HideWithSlideAndFade(BorderTools);
            AnimationsHelper.HideWithSlideAndFade(BoardBorderTools);

            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                InitialDirectory = Settings.Automation.AutoSavedStrokesLocation,
                Title = "打开墨迹文件",
                Filter = "FluentCanvas Files (*.icart;*.icstk)|*.icart;*.icstk|FluentCanvas Files (*.icart)|*.icart|FluentCanvas Stroke Files (*.icstk)|*.icstk"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                LoadInkCanvasFile(openFileDialog.FileName);
            }
        }

        public void LoadInkCanvasFile(string filePath)
        {
            var previousReason = _currentCommitType;
            try
            {
                _currentCommitType = CommitReason.CodeInput;
                CanvasDocument.LoadInto(filePath, inkCanvas, timeMachine);
                LogHelper.WriteLogToFile($"Opened {Path.GetExtension(filePath).ToLowerInvariant()}: {inkCanvas.Strokes.Count} strokes, {inkCanvas.Children.Count} elements", LogHelper.LogType.Event);
            }
            catch (Exception ex)
            {
                ShowNotificationAsync("作品打开失败：" + ex.Message);
                LogHelper.WriteLogToFile("打开作品失败 | " + ex, LogHelper.LogType.Error);
                return;
            }
            finally
            {
                _currentCommitType = previousReason;
            }

            if (inkCanvas.Visibility != Visibility.Visible) SymbolIconCursor_Click(null, null);
        }
    }
}
