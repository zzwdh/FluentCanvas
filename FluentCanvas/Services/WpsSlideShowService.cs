using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FluentCanvas.Services;

internal sealed class WpsSlideShowService : ISlideShowService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1);
    private readonly WpsBridgeTransport transport = WpsBridgeTransport.ForCurrentUser();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = PollInterval };
    private WpsBridgeTransport.State state;
    private WpsBridgeTransport.Command pending;
    private DateTimeOffset lastObserved;
    private CancellationTokenSource commands;
    private int generation;
    private bool reading;

    public WpsSlideShowService()
    {
        timer.Tick += ReadState;
    }

    public PowerPointSlide CurrentSlide { get; private set; }
    internal bool IsReportedActive => state?.Active == true;
    public event Action<PowerPointSlide> SlideChanged;
    public event Action SlideShowEnded;
    public event Action SlideLeaving { add { } remove { } }
    internal event Action<string> CommandFailed;

    internal void SetEnabled(bool enabled)
    {
        if (timer.IsEnabled == enabled) return;
        generation++;
        if (enabled)
        {
            commands = new CancellationTokenSource();
            timer.Start();
            ReadState(this, EventArgs.Empty);
            return;
        }
        timer.Stop();
        commands.Cancel();
        commands.Dispose();
        commands = null;
        pending = null;
        SetState(null);
    }

    private async void ReadState(object sender, EventArgs e)
    {
        if (reading || !timer.IsEnabled) return;
        reading = true;
        int currentGeneration = generation;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var (next, ambiguous) = await Task.Run(() => transport.Read(now));
            if (currentGeneration != generation) return;
            if (pending != null)
            {
                if (next is { } && next.InstanceId == pending.InstanceId && next.SessionId == pending.SessionId &&
                    next.AckId == pending.Id)
                {
                    pending = null;
                    if (!next.AckSucceeded) CommandFailed?.Invoke(UiText.Get("Presentation.WpsCommandFailed"));
                }
                else if (now.ToUnixTimeMilliseconds() >= pending.ExpiresAt)
                {
                    var expired = pending;
                    pending = null;
                    if (expired.Action != "exit" || next != null)
                        CommandFailed?.Invoke(UiText.Get("Presentation.WpsCommandNotConfirmed"));
                }
            }
            if (next != null)
            {
                lastObserved = now;
                SetState(next);
            }
            // The add-in can be replacing the message during this read. A short grace period
            // preserves the session through a partial write or a momentary host transition.
            else if (ambiguous || now - lastObserved > TimeSpan.FromMilliseconds(400))
                SetState(null);
            timer.Interval = CurrentSlide == null && pending == null ? IdleInterval : PollInterval;
        }
        finally { reading = false; }
    }

    private void SetState(WpsBridgeTransport.State next)
    {
        state = next;
        var previous = CurrentSlide;
        CurrentSlide = next == null ? null : new PowerPointSlide(
            next.SessionId.ToString("N"), UiText.Get("Presentation.WpsName"), next.SlideId, next.SlideIndex, next.SlideIndex,
            next.SlideCount, Provider: PresentationProvider.Wps, CanPersistInk: false, SessionId: next.SessionId);
        if (previous != null && CurrentSlide == null) SlideShowEnded?.Invoke();
        if (CurrentSlide != null && CurrentSlide != previous) SlideChanged?.Invoke(CurrentSlide);
    }

    public bool NextSlide() => Send("next", state?.CanNext == true);
    public bool PreviousSlide() => Send("previous", state?.CanPrevious == true);
    public bool EndShow() => Send("exit", state?.CanExit == true);
    public bool ShowSlideNavigation() => false;

    private bool Send(string action, bool available)
    {
        if (!available || pending != null || !timer.IsEnabled) return false;
        var now = DateTimeOffset.UtcNow;
        if (now - lastObserved >= TimeSpan.FromMilliseconds(500)) return false;
        var command = new WpsBridgeTransport.Command(WpsBridgeTransport.Protocol, state.InstanceId,
            state.SessionId, Guid.NewGuid(), action, now.Add(WpsBridgeTransport.Lifetime).ToUnixTimeMilliseconds());
        pending = command;
        _ = SendAsync(command, commands.Token);
        return true;
    }

    private async Task SendAsync(WpsBridgeTransport.Command command, CancellationToken cancellationToken)
    {
        try { await Task.Run(() => transport.Write(command, cancellationToken), cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (pending?.Id != command.Id) return;
            pending = null;
            LogHelper.WriteLogToFile($"WPS command mailbox: {ex.GetType().Name}", LogHelper.LogType.Trace);
            CommandFailed?.Invoke(UiText.Get("Presentation.WpsCommandNotSent"));
        }
    }
}
