using System;
using FluentCanvas.Localization;
using FluentCanvas.Helpers;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        #region Image
        private async void BtnImageInsert_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                long context = canvasContextVersion;
                string filePath = await WinUiPickers.PickOpenFileAsync(new WindowInteropHelper(this).Handle,
                    "FluentCanvas.ImportImage", new[] { ".jpg", ".jpeg", ".png", ".bmp" });
                if (filePath == null) return;

                bool compress = Settings.Canvas.IsCompressPicturesUploaded;
                var bitmap = await Task.Run(() => CanvasDocument.ReadImageForImport(filePath, compress));
                if (!CanFinishMediaImport(context)) return;

                var image = new Image
                {
                    Name = $"img_{Guid.NewGuid():N}",
                    Source = bitmap,
                    Width = bitmap.PixelWidth,
                    Height = bitmap.PixelHeight
                };
                InsertCanvasElement(image);
            }
            catch (Exception ex)
            {
                ShowNotificationAsync(UiText.Get("Actions.InsertImageFailed"));
                LogHelper.WriteLogToFile("插入图片失败 | " + ex, LogHelper.LogType.Error);
            }
        }

        #endregion

        #region Media
        private async void BtnMediaInsert_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                long context = canvasContextVersion;
                string filePath = await WinUiPickers.PickOpenFileAsync(new WindowInteropHelper(this).Handle,
                    "FluentCanvas.ImportVideo", new[] { ".mp4", ".avi", ".wmv" });
                if (filePath == null) return;

                string name = $"media_{Guid.NewGuid():N}";
                string location = Settings.Automation.AutoSavedStrokesLocation;
                string cachedFile = await Task.Run(() =>
                {
                    string folder = Path.Combine(StorageHelper.NormalizeStorageLocation(location), "File Dependency");
                    Directory.CreateDirectory(folder);
                    string destination = Path.Combine(folder, name + Path.GetExtension(filePath));
                    CanvasDocument.CopyVideoForImport(filePath, destination);
                    return destination;
                });
                if (!CanFinishMediaImport(context))
                {
                    await Task.Run(() => File.Delete(cachedFile));
                    return;
                }

                var media = new MediaElement
                {
                    Name = name,
                    Source = new Uri(cachedFile),
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    Width = 256,
                    Height = 256
                };
                media.Loaded += async (_, _) =>
                {
                    media.Play();
                    await Task.Delay(100);
                    if (media.IsLoaded) media.Pause();
                };
                InsertCanvasElement(media);
            }
            catch (Exception ex)
            {
                ShowNotificationAsync(UiText.Get("Actions.InsertVideoFailed"));
                LogHelper.WriteLogToFile("插入视频失败 | " + ex, LogHelper.LogType.Error);
            }
        }

        #endregion

        private bool CanFinishMediaImport(long context)
        {
            if (context == canvasContextVersion) return true;
            ShowNotificationAsync(UiText.Get("Actions.CanvasChangedDuringImport"));
            return false;
        }

        private void InsertCanvasElement(FrameworkElement element)
        {
            CenterAndScaleElement(element);
            InkCanvas.SetLeft(element, 0);
            InkCanvas.SetTop(element, 0);
            inkCanvas.Children.Add(element);
            timeMachine.CommitElementInsertHistory(element);
        }

        private void CenterAndScaleElement(FrameworkElement element)
        {
            double maxWidth = SystemParameters.PrimaryScreenWidth / 2;
            double maxHeight = SystemParameters.PrimaryScreenHeight / 2;

            double scaleX = maxWidth / element.Width;
            double scaleY = maxHeight / element.Height;
            double scale = Math.Min(scaleX, scaleY);

            TransformGroup transformGroup = new TransformGroup();
            transformGroup.Children.Add(new ScaleTransform(scale, scale));

            double canvasWidth = inkCanvas.ActualWidth;
            double canvasHeight = inkCanvas.ActualHeight;
            double centerX = (canvasWidth - element.Width * scale) / 2;
            double centerY = (canvasHeight - element.Height * scale) / 2;

            transformGroup.Children.Add(new TranslateTransform(centerX, centerY));

            element.RenderTransform = transformGroup;
        }
    }
}
