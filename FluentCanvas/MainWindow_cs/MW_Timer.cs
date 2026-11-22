using FluentCanvas.Helpers;
using System;
using System.Windows;
using System.Windows.Threading;

namespace FluentCanvas;

public partial class MainWindow
{
    private readonly DispatcherTimer autoFoldTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(1500)
    };
    private bool isHidingSubPanelsWhenInking;
    private bool foldFloatingBarByUser;
    private bool unfoldFloatingBarByUser;

    private void InitTimers()
    {
        autoFoldTimer.Tick += (_, _) =>
        {
            if (activePowerPointSlide == null && !isFloatingBarChangingHideMode) UpdateAutomaticToolbarFold();
        };
        StateChanged += (_, _) => QueueAutomaticToolbarFoldUpdate();
        IsEnabledChanged += (_, _) => QueueAutomaticToolbarFoldUpdate();
        Closed += (_, _) => autoFoldTimer.Stop();
        InitFloatingToolbarPlacement();
    }

    private void StartOrStoptimerCheckAutoFold()
    {
        autoFoldTimer.IsEnabled = Settings.Automation.IsForegroundAutoFoldEnabled;
        QueueAutomaticToolbarFoldUpdate();
    }

    private void QueueAutomaticToolbarFoldUpdate()
    {
        if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(UpdateAutomaticToolbarFold);
    }

    private void UpdateAutomaticToolbarFold()
    {
        if (!IsLoaded || !IsEnabled || WindowState == WindowState.Minimized) return;

        var automation = Settings.Automation;
        bool shouldFold = automation.IsAutoFoldInPPTSlideShow;
        if (activePowerPointSlide == null)
        {
            shouldFold = false;
            if (automation.IsForegroundAutoFoldEnabled)
            {
                if (ForegroundWindowInfo.Capture(automation.IsAutoFoldInMSWhiteboard) is not { } window) return;
                shouldFold =
                    (automation.IsAutoFoldInEasiNote && Named("EasiNote") &&
                     !(automation.IsAutoFoldInEasiNoteIgnoreDesktopAnno && window.Title.Length == 0 && window.Height is < 500)) ||
                    (automation.IsAutoFoldInEasiCamera && Named("EasiCamera")) ||
                    (automation.IsAutoFoldInEasiNote3C && Named("EasiNote")) ||
                    (automation.IsAutoFoldInSeewoPincoTeacher && (Named("BoardService") || Named("seewoPincoTeacher"))) ||
                    (automation.IsAutoFoldInHiteCamera && Named("HiteCamera")) ||
                    (automation.IsAutoFoldInHiteTouchPro && Named("HiteTouchPro")) ||
                    (automation.IsAutoFoldInWxBoardMain && Named("WxBoardMain")) ||
                    (automation.IsAutoFoldInOldZyBoard && window.Title is "WhiteBoard - DrawingWindow" or "InstantAnnotationWindow") ||
                    (automation.IsAutoFoldInMSWhiteboard && window.IsMicrosoftWhiteboard);

                bool Named(string name) => window.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase);
            }

            if (!shouldFold) unfoldFloatingBarByUser = false;
        }

        if (shouldFold != isFloatingBarFolded && !(shouldFold ? unfoldFloatingBarByUser : foldFloatingBarByUser))
            ApplyFloatingBarFold(shouldFold, byUser: false);
    }
}
