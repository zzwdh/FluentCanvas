using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using FluentCanvas.Services;
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        private readonly ScreenshotWriteQueue screenshotWrites = new();
        private Guid? lastSlideCaptureFailure;

        private void DrainScreenshotWrites() => screenshotWrites.Drain();

        private async void SaveScreenshot(bool isHideNotification, bool saveEditable = true)
        {
            if (currentMode == 0 && activePowerPointSlide is { } slide)
            {
                SavePPTScreenshot(slide, requested: !isHideNotification, saveEditable);
                return;
            }

            try
            {
                var bitmap = CaptureDesktopScreenshot();
                string path = await SaveScreenshotInStorageAsync(stream => bitmap.Save(stream, ImageFormat.Png),
                    "Auto Saved - Screenshots", NewScreenshotName(),
                    saveEditable && Settings.Automation.IsAutoSaveStrokesAtScreenshot, bitmap);
                if (!isHideNotification && !closingPowerPointOwner)
                    ShowNotificationAsync(UiText.Format("Actions.ScreenshotSaved", path));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"SaveScreenshot failed: {ex}", LogHelper.LogType.Error);
                if (!isHideNotification && !closingPowerPointOwner)
                    ShowNotificationAsync(UiText.Get("Actions.ScreenshotPathFailed"));
            }
        }

        private async void SaveScreenShotToDesktop()
        {
            try
            {
                string savePath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string fileName = NewScreenshotName() + ".png";
                string destination = Path.Combine(savePath, fileName);
                var bitmap = CaptureDesktopScreenshot();
                var write = screenshotWrites.Enqueue(() =>
                {
                    using (bitmap) AtomicFile.Write(destination, stream => bitmap.Save(stream, ImageFormat.Png));
                });
                if (Settings.Automation.IsAutoSaveStrokesAtScreenshot)
                    _ = SaveInkCanvasFileAsync(newNotice: false, screenshotWrite: write);
                await write;
                if (!closingPowerPointOwner) ShowNotificationAsync(UiText.Format("Actions.ScreenshotSaved", destination));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"SaveScreenShotToDesktop failed: {ex}", LogHelper.LogType.Error);
                if (!closingPowerPointOwner) ShowNotificationAsync(UiText.Get("Actions.ScreenshotFailed"));
            }
        }

        private async void SavePPTScreenshot(PowerPointSlide slide, bool requested, bool saveEditable = true)
        {
            nint showWindow = slide.WindowHandle;
            string fileName = $"{slide.DeckKey}/{slide.SlideId}-{NewScreenshotName()}";
            long context = canvasContextVersion;
            BitmapSource screenshot;
            try
            {
                if (!PresentationWindow.TryGetCaptureBounds(showWindow, out var bounds))
                    throw new InvalidOperationException("The slide window is not available for capture.");

                var capture = WindowCapture.CaptureAsync(showWindow);
                // Automatic capture is called before Office advances. The frame must
                // be detached before returning control to the slideshow transition.
                var frame = requested ? await capture : capture.GetAwaiter().GetResult();
                if (closingPowerPointOwner) return;
                if (activePowerPointSlide != slide || canvasContextVersion != context || currentMode != 0 ||
                    !PresentationWindow.TryGetCaptureBounds(showWindow, out var currentBounds) || currentBounds != bounds)
                    throw new InvalidOperationException("The slide or its position changed during capture.");

                screenshot = PresentationWindow.ComposeScreenshot(frame, bounds, inkCanvas);
            }
            catch (Exception ex)
            {
                ReportSlideScreenshotFailure(slide.SessionId, requested, "Presentation.SlideShowWindowUnavailable", ex);
                return;
            }

            try
            {
                await SaveScreenshotInStorageAsync(stream =>
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(screenshot));
                    encoder.Save(stream);
                }, "Auto Saved - PPT Screenshots", fileName,
                    saveEditable && Settings.Automation.IsAutoSaveStrokesAtScreenshot);
            }
            catch (Exception ex)
            {
                ReportSlideScreenshotFailure(slide.SessionId, requested, "Actions.SlideScreenshotFailed", ex);
            }
        }

        private void ReportSlideScreenshotFailure(Guid session, bool requested, string message, Exception failure)
        {
            LogHelper.WriteLogToFile($"SavePPTScreenshot failed: {failure}", LogHelper.LogType.Error);
            if (closingPowerPointOwner || (!requested && session == lastSlideCaptureFailure)) return;
            lastSlideCaptureFailure = session;
            ShowNotificationAsync(UiText.Get(message));
        }

        private async Task<string> SaveScreenshotInStorageAsync(Action<Stream> writePng, string folder, string fileName,
            bool saveEditable, Drawing.Bitmap ownedDesktopBitmap = null)
        {
            string configured = Settings.Automation.AutoSavedStrokesLocation;
            string date = Settings.Automation.IsSaveScreenshotsInDateFolders ? DateTime.Now.ToString("yyyy-MM-dd") : "";
            var write = screenshotWrites.Enqueue(() =>
            {
                using (ownedDesktopBitmap)
                {
                    string root = StorageHelper.NormalizeStorageLocation(configured);
                    string destination = Path.Combine(root, folder, date, fileName + ".png");
                    StorageHelper.EnsureDirectory(Path.GetDirectoryName(destination));
                    AtomicFile.Write(destination, writePng);
                    return (Root: root, Path: destination);
                }
            });

            // Snapshot the same editable page before returning to the slideshow, even when
            // the PNG and editable document will reach disk after the page changes.
            if (saveEditable) _ = SaveInkCanvasFileAsync(newNotice: false, screenshotWrite: write);
            var saved = await write;
            if (!closingPowerPointOwner && Settings.Automation.AutoSavedStrokesLocation == configured)
                Settings.Automation.AutoSavedStrokesLocation = saved.Root;
            return saved.Path;
        }

        private static string NewScreenshotName() => $"{DateTime.Now:yyyy-MM-dd HH-mm-ss-fff}-{Guid.NewGuid():N}";

        private static Drawing.Bitmap CaptureDesktopScreenshot()
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            var bitmap = new Drawing.Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Drawing.Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(screen.X, screen.Y, 0, 0, screen.Size, Drawing.CopyPixelOperation.SourceCopy);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
    }
}
