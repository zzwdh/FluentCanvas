using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using FluentCanvas.Services;
using Microsoft.Office.Core;
using Microsoft.Office.Interop.PowerPoint;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PixelRectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        private readonly IPowerPointService pptService;
        private readonly PresentationSlideShowService slideShows;
        private PowerPointSlide activePowerPointSlide;
        private PowerPointInkStorage powerPointInk;
        private PowerPointInkWriter powerPointWriter;
        private CanvasPage desktopBeforePowerPoint;
        private DispatcherTimer presentationDisplayTimer;
        private PixelRectangle? desktopBoundsBeforePresentation;
        private DateTimeOffset lastPowerPointTransitionCapture;
        private bool closingPowerPointOwner;
        private readonly Dictionary<(string Deck, int Slide), CanvasPage> powerPointPages = new();

        private bool promptingForHiddenSlides;

        private void InitPowerPoint()
        {
            powerPointWriter = new PowerPointInkWriter(ReportPowerPointWrite);
            pptService.PresentationAvailable += OnPowerPointPresentationAvailable;
            slideShows.SlideLeaving += CaptureLeavingPowerPointSlide;
            slideShows.SlideChanged += ApplyPowerPointSlide;
            slideShows.SlideShowEnded += FinishPowerPointSlideShow;
            slideShows.CommandFailed += ShowPresentationCommandFailure;
            presentationDisplayTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            presentationDisplayTimer.Tick += (_, _) => FollowPresentationDisplay();
            Closed += (_, _) =>
            {
                closingPowerPointOwner = true;
                presentationDisplayTimer.Stop();
                slideShows.SetEnabled(false, false);
                pptService.PresentationAvailable -= OnPowerPointPresentationAvailable;
                slideShows.SlideLeaving -= CaptureLeavingPowerPointSlide;
                slideShows.SlideChanged -= ApplyPowerPointSlide;
                slideShows.SlideShowEnded -= FinishPowerPointSlideShow;
                slideShows.CommandFailed -= ShowPresentationCommandFailure;
                powerPointWriter.Close();
                DrainScreenshotWrites();
            };

            navigation.NextRequested += (s, e) => BtnPPTSlidesDown_Click(null, null);
            navigation.PreviousRequested += (s, e) => BtnPPTSlidesUp_Click(null, null);
            navigation.NavigationToggleRequested += OnPptNavigationToggleRequested;
        }

        internal void ApplyPresentationSettings() => slideShows.SetEnabled(
            Settings.PowerPointSettings.PowerPointSupport, Settings.PowerPointSettings.IsSupportWPS);

        private void ShowPresentationCommandFailure(string message) => ShowNotificationAsync(message);

        private async void OnPptNavigationToggleRequested(object sender, EventArgs e)
        {
            if (activePowerPointSlide?.Provider != PresentationProvider.PowerPoint) return;
            Main_Grid.Background = new SolidColorBrush(StringToColor("#01FFFFFF"));
            CursorIcon_Click(null, null);
            if (!slideShows.ShowSlideNavigation()) ShowNotificationAsync(UiText.Get("Presentation.NavigationFailed"));
            if (!isFloatingBarFolded)
            {
                await Task.Delay(100);
                ViewboxFloatingBarMarginAnimation();
            }
        }

        private PowerPointInkStorage CreatePowerPointInkStorage() =>
            new(StorageHelper.NormalizeStorageLocation(Settings.Automation.AutoSavedStrokesLocation), StorageHelper.UserDataPath);

        private void OnPowerPointPresentationAvailable(Presentation presentation)
        {
            try
            {
                var settings = Settings.PowerPointSettings;
                var slides = presentation.Slides.Cast<Slide>().ToArray();
                string deck = pptService.GetDeckKey(presentation);
                var storage = CreatePowerPointInkStorage();
                if (settings.IsAutoSaveStrokesInPowerPoint && !string.IsNullOrWhiteSpace(presentation.Path))
                {
                    string legacyFolder = storage.GetLegacyFolder(deck, presentation.Name, slides.Length);
                    if (legacyFolder != null)
                    {
                        ConfirmPowerPointChange(presentation,
                            UiText.Get("Presentation.ImportLegacyPrompt"),
                            () =>
                            {
                                int count = storage.ImportLegacy(deck, legacyFolder, slides.Select(slide => slide.SlideID).ToArray(),
                                    candidateId => powerPointWriter.PendingInk(deck, candidateId) != null);
                                ShowNotificationAsync(UiText.Format("Presentation.ImportedLegacy", count));
                            });
                    }
                }

                if (settings.IsNotifyPreviousPage && (powerPointWriter.PendingLastSlide(deck) ?? storage.ReadLastSlide(deck)) is int slideId)
                {
                    var slide = slides.FirstOrDefault(candidate => candidate.SlideID == slideId);
                    if (slide != null)
                    {
                        int index = slide.SlideIndex;
                        ConfirmPowerPointChange(presentation, UiText.Format("Presentation.LastSlidePrompt", index),
                            () => pptService.GoToSlide(presentation, index));
                    }
                }

                if (settings.IsNotifyHiddenPage && !promptingForHiddenSlides)
                {
                    var hidden = slides.Where(slide => slide.SlideShowTransition.Hidden == MsoTriState.msoTrue).ToArray();
                    if (hidden.Length > 0 && pptService.IsCurrentPresentation(presentation))
                    {
                        promptingForHiddenSlides = true;
                        try
                        {
                            ConfirmPowerPointChange(presentation, UiText.Get("Presentation.HiddenSlidesPrompt"), () =>
                            {
                                foreach (var slide in hidden) slide.SlideShowTransition.Hidden = MsoTriState.msoFalse;
                            });
                        }
                        finally { promptingForHiddenSlides = false; }
                    }
                }

                if (settings.IsNotifyAutoPlayPresentation &&
                    presentation.SlideShowSettings.AdvanceMode != PpSlideShowAdvanceMode.ppSlideShowManualAdvance &&
                    slides.Any(slide => slide.SlideShowTransition.AdvanceOnTime == MsoTriState.msoTrue && slide.SlideShowTransition.AdvanceTime > 0))
                {
                    ConfirmPowerPointChange(presentation, UiText.Get("Presentation.ManualAdvancePrompt"),
                        () => presentation.SlideShowSettings.AdvanceMode = PpSlideShowAdvanceMode.ppSlideShowManualAdvance);
                }
            }
            catch (Exception ex) when (ex is COMException or IOException or ArgumentException or UnauthorizedAccessException or UriFormatException)
            {
                LogHelper.WriteLogToFile($"PowerPoint presentation inspection: {ex}", LogHelper.LogType.Error);
            }
        }

        private void ConfirmPowerPointChange(Presentation presentation, string message, Action confirmed)
        {
            if (!pptService.IsCurrentPresentation(presentation)) return;
            var dialog = new YesOrNoNotificationWindow(message, GetMainWindowTheme() == "Light");
            if (dialog.ShowDialog() != true || !pptService.IsCurrentPresentation(presentation)) return;
            try { confirmed(); }
            catch (Exception ex) when (ex is COMException or IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            {
                ShowNotificationAsync(UiText.Format("Presentation.OperationFailed", ex.Message));
                LogHelper.WriteLogToFile($"PowerPoint requested change: {ex}", LogHelper.LogType.Error);
            }
        }

        private void ApplyPowerPointSlide(PowerPointSlide next)
        {
            try
            {
                var previous = activePowerPointSlide;
                if (previous != null && (previous.Provider != next.Provider || previous.SessionId != next.SessionId))
                {
                    FinishPowerPointSlideShow();
                    previous = null;
                }
                if (previous == null)
                    BeginPowerPointSlideShow();
                if (previous == null || previous.DeckKey != next.DeckKey || previous.SlideId != next.SlideId)
                {
                    if (previous != null) SaveActivePowerPointSlide();
                    RestorePowerPointSlide(next);
                    lastPowerPointTransitionCapture = default;
                }
                activePowerPointSlide = next;
                if (previous == null)
                {
                    unfoldFloatingBarByUser = false;
                    QueueAutomaticToolbarFoldUpdate();
                }
                FollowPresentationDisplay(raiseOverlay: previous?.WindowHandle != next.WindowHandle);
                presentationDisplayTimer.IsEnabled = next.WindowHandle != 0;
                navigation.CanOpenSlideNavigation = next.Provider == PresentationProvider.PowerPoint;
                navigation.SetPosition(next.ShowPosition, next.SlideCount, next.SlideIndex);
            }
            catch (Exception ex)
            {
                ShowNotificationAsync(UiText.Format("Presentation.AnnotationPageFailed", ex.Message));
                LogHelper.WriteLogToFile($"PowerPoint annotation page: {ex}", LogHelper.LogType.Error);
            }
        }

        private void BeginPowerPointSlideShow()
        {
            if (powerPointInk == null || !powerPointWriter.HasPendingWrites)
                powerPointInk = CreatePowerPointInkStorage();
            desktopBeforePowerPoint = CaptureDesktopCanvas();
            powerPointPages.Clear();
            BtnPPTSlideShowEnd.Visibility = Visibility.Visible;
            navigation.IsActive = true;
            if (currentMode == 0)
                navigation.ShowPanels(Settings.PowerPointSettings.IsShowBottomPPTNavigationPanel,
                    Settings.PowerPointSettings.IsShowSidePPTNavigationPanel);
            ViewboxFloatingBar.Opacity = Settings.Appearance.IsColorfulViewboxFloatingBar ? 0.8 : 1;
            BorderFloatingBarMainControls.Visibility = Visibility.Visible;
            if (currentMode == 0 && Settings.PowerPointSettings.IsShowCanvasAtNewSlideShow)
            {
                if (Main_Grid.Background == Brushes.Transparent) BtnHideInkCanvas_Click(null, null);
                BtnColorRed_Click(null, null);
            }
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ViewboxFloatingBarMarginAnimation));
        }

        private void FollowPresentationDisplay(bool raiseOverlay = false)
        {
            if (WindowState == WindowState.Minimized || activePowerPointSlide is not { WindowHandle: not 0 } slide ||
                !PresentationWindow.TryGetClientBounds(slide.WindowHandle, out _))
                return;

            nint overlay = new WindowInteropHelper(this).Handle;
            var target = Screen.FromHandle(slide.WindowHandle).Bounds;
            bool aboveShow = Topmost && currentMode == 0;
            if (!PresentationWindow.TryGetWindowBounds(overlay, out var origin) ||
                (origin == target && !(raiseOverlay && aboveShow)) ||
                !PresentationWindow.TryMove(overlay, target, aboveShow))
                return;

            if (origin != target) desktopBoundsBeforePresentation ??= origin;
            ViewboxFloatingBarMarginAnimation();
        }

        private void RestorePresentationDisplay()
        {
            presentationDisplayTimer.Stop();
            if (desktopBoundsBeforePresentation is not { } origin) return;
            desktopBoundsBeforePresentation = null;
            if (closingPowerPointOwner || WindowState == WindowState.Minimized) return;

            var display = Screen.FromRectangle(origin);
            if (!display.Bounds.IntersectsWith(origin)) origin = display.WorkingArea;
            PresentationWindow.TryMove(new WindowInteropHelper(this).Handle, origin);
        }

        private static CanvasPage CreatePowerPointCanvas(StrokeCollection strokes)
        {
            var history = new TimeMachine.Snapshot(Array.Empty<TimeMachineHistory>(), -1, new object(), null);
            return new CanvasPage(strokes, Array.Empty<UIElement>(), history);
        }

        private CanvasPage CaptureDesktopCanvas() => Pages.ReadPage(0) ?? CreatePowerPointCanvas(new StrokeCollection());

        private void RestoreDesktopCanvas(CanvasPage state) => Pages.ReplaceDesktop(state);

        private void RestorePowerPointSlide(PowerPointSlide slide)
        {
            var key = (slide.DeckKey, slide.SlideId);
            if (powerPointPages.TryGetValue(key, out var state))
            {
                RestoreDesktopCanvas(state);
                return;
            }

            var strokes = new StrokeCollection();
            if (slide.CanPersistInk && Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint)
            {
                try
                {
                    if (powerPointWriter.PendingInk(key.DeckKey, key.SlideId) is { } pending)
                    {
                        using var input = new MemoryStream(pending);
                        strokes = new StrokeCollection(input);
                    }
                    else
                    {
                        string path = powerPointInk.LatestSlidePath(slide.DeckKey, slide.SlideId);
                        if (File.Exists(path))
                            strokes = CanvasDocument.LoadSlideInk(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                {
                    ShowNotificationAsync(UiText.Format("Presentation.ReadAnnotationFailed", slide.SlideIndex));
                    LogHelper.WriteLogToFile($"PowerPoint annotation read: {ex}", LogHelper.LogType.Error);
                }
            }
            // Reopened strokes belong to the base state. Start undo history here.
            var loaded = CreatePowerPointCanvas(strokes);
            powerPointPages[key] = loaded;
            RestoreDesktopCanvas(loaded);
        }

        private void SaveActivePowerPointSlide()
        {
            var slide = activePowerPointSlide;
            var key = (slide.DeckKey, slide.SlideId);
            bool alreadyHadInk = powerPointPages.TryGetValue(key, out var previous) && previous.Strokes.Count > 0;
            var state = CaptureDesktopCanvas();
            powerPointPages[key] = state;
            if (!slide.CanPersistInk || !Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint ||
                (state.Strokes.Count == 0 && !alreadyHadInk))
                return;
            using var ink = new MemoryStream();
            state.Strokes.Save(ink);
            powerPointWriter.SaveSlide(powerPointInk, key.DeckKey, key.SlideId, ink.ToArray());
        }

        private void ReportPowerPointWrite(PowerPointInkWriter.Result result)
        {
            if (result.Error != null)
                LogHelper.WriteLogToFile($"PowerPoint annotation save: {result.Error}", LogHelper.LogType.Error);

            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (closingPowerPointOwner) return;
                if (result.Error != null)
                    ShowNotificationAsync(result.IsPosition
                        ? UiText.Format("Presentation.SaveSlideFailed", result.Error.Message)
                        : UiText.Get("Presentation.SaveAnnotationFailed"));
                else if (result.UsedRecovery)
                    ShowNotificationAsync(UiText.Get("Presentation.SavedToRecovery"));
            }));
        }

        private void FinishPowerPointSlideShow()
        {
            var finished = activePowerPointSlide;
            if (finished == null) return;
            try
            {
                SaveActivePowerPointSlide();
                if (finished.CanPersistInk &&
                    (Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint || Settings.PowerPointSettings.IsNotifyPreviousPage))
                    powerPointWriter.SaveLastSlide(powerPointInk, finished.DeckKey, finished.SlideId);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                ShowNotificationAsync(UiText.Format("Presentation.SaveSlideFailed", ex.Message));
                LogHelper.WriteLogToFile($"PowerPoint annotation finish: {ex}", LogHelper.LogType.Error);
            }
            finally
            {
                activePowerPointSlide = null;
                lastPowerPointTransitionCapture = default;
                RestorePresentationDisplay();
                if (!closingPowerPointOwner)
                {
                    // Switch the toolbar out of annotation mode before restoring desktop ink.
                    if (currentMode == 0 && Main_Grid.Background != Brushes.Transparent) BtnHideInkCanvas_Click(null, null);
                    BtnPPTSlideShowEnd.Visibility = Visibility.Collapsed;
                    navigation.HidePanels();
                    navigation.IsActive = false;
                    if (desktopBeforePowerPoint != null) RestoreDesktopCanvas(desktopBeforePowerPoint);
                    ViewboxFloatingBar.Opacity = Settings.Appearance.IsColorfulViewboxFloatingBar ? 0.95 : 1;
                    ViewboxFloatingBarMarginAnimation();
                    unfoldFloatingBarByUser = false;
                    QueueAutomaticToolbarFoldUpdate();
                }
                powerPointPages.Clear();
                desktopBeforePowerPoint = null;
            }
        }

        private void CaptureLeavingPowerPointSlide()
        {
            if (currentMode != 0 || activeCanvasPage != 0 ||
                !Settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint ||
                (inkCanvas.Strokes.Count <= Settings.Automation.MinimumAutomationStrokeNumber && inkCanvas.Children.Count == 0))
                return;

            var now = DateTimeOffset.UtcNow;
            if (now - lastPowerPointTransitionCapture < TimeSpan.FromMilliseconds(200)) return;
            lastPowerPointTransitionCapture = now;
            CaptureCurrentPowerPointSlide(requested: false);
        }

        private void CaptureCurrentPowerPointSlide(bool requested = true)
        {
            if (activePowerPointSlide is not { } slide || currentMode != 0) return;
            if (slide.WindowHandle == 0)
            {
                if (requested) ShowNotificationAsync(UiText.Get("Presentation.SlideShowWindowUnavailable"));
                return;
            }
            SavePPTScreenshot(slide, requested);
        }

        private string PresentationProviderName =>
            activePowerPointSlide?.Provider == PresentationProvider.Wps ? UiText.Get("Presentation.WpsName") : "PowerPoint";

        private void BtnPPTSlidesUp_Click(object sender, RoutedEventArgs e)
        {
            if (currentMode != 0) ImageBlackboard_Click(null, null);
            if (!slideShows.PreviousSlide()) ShowNotificationAsync(UiText.Format("Presentation.PreviousSlideFailed", PresentationProviderName));
        }

        private void BtnPPTSlidesDown_Click(object sender, RoutedEventArgs e)
        {
            if (currentMode != 0) ImageBlackboard_Click(null, null);
            if (!slideShows.NextSlide()) ShowNotificationAsync(UiText.Format("Presentation.NextSlideFailed", PresentationProviderName));
        }

        private void BtnPPTSlideShowEnd_Click(object sender, RoutedEventArgs e)
        {
            if (activePowerPointSlide != null && !slideShows.EndShow())
                ShowNotificationAsync(UiText.Format("Presentation.EndShowFailed", PresentationProviderName));
            HideSubPanels("cursor");
        }
    }
}
