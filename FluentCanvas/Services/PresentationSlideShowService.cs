using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace FluentCanvas.Services;

internal sealed class PresentationSlideShowService : ISlideShowService
{
    private readonly IPowerPointService powerPoint;
    private readonly WpsSlideShowService wps;
    private readonly DispatcherTimer selectionTimer;
    private bool configuring;

    public PresentationSlideShowService(IPowerPointService powerPoint, WpsSlideShowService wps)
    {
        this.powerPoint = powerPoint;
        this.wps = wps;
        selectionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        selectionTimer.Tick += (_, _) => RefreshSelection();
        powerPoint.SlideChanged += _ => RefreshSelection();
        powerPoint.SlideShowEnded += RefreshSelection;
        powerPoint.SlideLeaving += () =>
        {
            if (CurrentSlide?.Provider == PresentationProvider.PowerPoint)
                SlideLeaving?.Invoke();
        };
        wps.SlideChanged += _ => RefreshSelection();
        wps.SlideShowEnded += RefreshSelection;
        wps.CommandFailed += message => CommandFailed?.Invoke(message);
    }

    public PowerPointSlide CurrentSlide { get; private set; }
    public event Action<PowerPointSlide> SlideChanged;
    public event Action SlideLeaving;
    public event Action SlideShowEnded;
    internal event Action<string> CommandFailed;

    internal void SetEnabled(bool powerPointEnabled, bool wpsEnabled)
    {
        configuring = true;
        try
        {
            powerPoint.SetEnabled(powerPointEnabled);
            wps.SetEnabled(wpsEnabled);
        }
        finally
        {
            configuring = false;
            RefreshSelection();
        }
    }

    public bool NextSlide() => SelectedService?.NextSlide() == true;
    public bool PreviousSlide() => SelectedService?.PreviousSlide() == true;
    public bool EndShow() => SelectedService?.EndShow() == true;
    public bool ShowSlideNavigation() => SelectedService?.ShowSlideNavigation() == true;

    private ISlideShowService SelectedService => CurrentSlide?.Provider switch
    {
        PresentationProvider.PowerPoint => powerPoint,
        PresentationProvider.Wps => wps,
        _ => null
    };

    private void RefreshSelection()
    {
        if (configuring) return;
        var powerPointSlide = powerPoint.CurrentSlide;
        var wpsSlide = wps.CurrentSlide;
        var next = SelectSlide(powerPointSlide, wpsSlide);
        selectionTimer.IsEnabled = powerPointSlide != null && wpsSlide != null && next == null;
        if (next == CurrentSlide) return;
        if (CurrentSlide != null && (next == null || next.Provider != CurrentSlide.Provider ||
            next.SessionId != CurrentSlide.SessionId))
        {
            CurrentSlide = null;
            SlideShowEnded?.Invoke();
        }
        CurrentSlide = next;
        if (next != null) SlideChanged?.Invoke(next);
    }

    private PowerPointSlide SelectSlide(PowerPointSlide powerPointSlide, PowerPointSlide wpsSlide)
    {
        // Keep the selected session through focus changes so its in-memory ink survives.
        var existing = CurrentSlide?.Provider == PresentationProvider.PowerPoint ? powerPointSlide : wpsSlide;
        if (CurrentSlide != null && existing?.SessionId == CurrentSlide.SessionId)
            return existing;
        if (powerPointSlide == null || wpsSlide == null) return powerPointSlide ?? wpsSlide;
        var foreground = PInvoke.GetForegroundWindow();
        if (powerPointSlide.WindowHandle != 0 && powerPointSlide.WindowHandle == (nint)foreground)
            return powerPointSlide;
        if (wps.IsReportedActive && IsWpsForeground(foreground))
            return wpsSlide;
        return null;
    }

    private static bool IsWpsForeground(HWND foreground)
    {
        if (PInvoke.GetWindowThreadProcessId(foreground, out uint processId) == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("wpp", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
