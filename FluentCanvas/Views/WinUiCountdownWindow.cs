using System;
using System.Media;
using FluentCanvas.Classroom;
using FluentCanvas.Localization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace FluentCanvas.Views;

internal sealed class WinUiCountdownWindow
{
    private readonly Countdown countdown = new(TimeProvider.System);
    private readonly DispatcherQueueTimer timer;
    private readonly WinUiAccessibility accessibility;
    private readonly SoundPlayer alarm = new();
    private readonly TextBlock time = new()
    {
        Text = "00:01:00", FontFamily = new FontFamily("Consolas"), FontSize = 112,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
        FlowDirection = FlowDirection.LeftToRight
    };
    private readonly TextBlock status = WinUiClassroomTools.Label(UiText.Get("Actions.Ready"), 14);
    private readonly ProgressBar progress = new() { Maximum = 1, Value = 1, Height = 6 };
    private readonly NumberBox hours = TimePart(UiText.Get("Actions.Hours"), 0, 99);
    private readonly NumberBox minutes = TimePart(UiText.Get("Actions.Minutes"), 1, 59);
    private readonly NumberBox seconds = TimePart(UiText.Get("Actions.Seconds"), 0, 59);
    private readonly Button start = WinUiClassroomTools.Button(UiText.Get("Actions.Start"), accent: true);
    private readonly Button reset = WinUiClassroomTools.Button(UiText.Get("Actions.Reset"));
    private readonly Button compactButton = WinUiClassroomTools.Button(UiText.Get("Actions.Compact"));
    private readonly Button fullScreenButton = WinUiClassroomTools.Button(UiText.Get("Actions.FullScreen"));
    private readonly StackPanel durationControls = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock heading = WinUiClassroomTools.Label(UiText.Get("Canvas.Countdown"), 28);
    private readonly Border display;
    private readonly StackPanel panel;
    private bool compact;
    private bool fullScreen;

    internal Window Window { get; }

    internal WinUiCountdownWindow()
    {
        Window = WinUiClassroomTools.CreateWindow(UiText.Get("Canvas.Countdown"));
        accessibility = new WinUiAccessibility(Window);
        AutomationProperties.SetName(time, UiText.Get("Actions.TimeRemaining"));
        AutomationProperties.SetName(progress, UiText.Get("Actions.TimeRemainingProgress"));
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(heading);
        var displayControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        displayControls.Children.Add(compactButton);
        displayControls.Children.Add(fullScreenButton);
        Grid.SetColumn(displayControls, 1);
        header.Children.Add(displayControls);

        var timeLabel = WinUiClassroomTools.Label(UiText.Get("Actions.TimeRemaining"), 14);
        timeLabel.HorizontalAlignment = HorizontalAlignment.Center;
        status.HorizontalAlignment = HorizontalAlignment.Center;
        var remaining = new StackPanel { Spacing = 9 };
        remaining.Children.Add(timeLabel);
        remaining.Children.Add(new Viewbox { MaxWidth = 700, StretchDirection = StretchDirection.DownOnly, Stretch = Stretch.Uniform, Child = time });
        remaining.Children.Add(progress);
        remaining.Children.Add(status);
        display = accessibility.CreateCard(remaining, new Thickness(30, 26, 30, 24));
        accessibility.AddEntrance(display);

        foreach (var input in new[] { hours, minutes, seconds })
        {
            durationControls.Children.Add(input);
            input.ValueChanged += (_, _) =>
            {
                if (countdown.State == CountdownState.Ready) ShowRemaining(ReadDuration(), updateProgress: false);
            };
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 10 };
        start.MinWidth = 100;
        reset.MinWidth = 76;
        actions.Children.Add(start);
        actions.Children.Add(reset);
        panel = new StackPanel { Padding = new Thickness(28, 18, 28, 18), Spacing = 18, MaxWidth = 940 };
        panel.Children.Add(header);
        panel.Children.Add(display);
        panel.Children.Add(durationControls);
        panel.Children.Add(actions);

        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Window.Content = scroll;
        start.Click += (_, _) => ToggleTimer();
        reset.Click += (_, _) => Reset();
        compactButton.Click += (_, _) => SetCompact(!compact);
        fullScreenButton.Click += (_, _) => SetFullScreen(!fullScreen);
        AddShortcut(scroll, VirtualKey.F11, () => SetFullScreen(!fullScreen));
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            if (!fullScreen) return;
            SetFullScreen(false);
            args.Handled = true;
        };
        scroll.KeyboardAccelerators.Add(escape);
        timer = Window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => Tick();
        Window.Closed += (_, _) =>
        {
            timer.Stop();
            alarm.Stop();
            alarm.Stream?.Dispose();
            alarm.Dispose();
        };
        WinUiClassroomTools.Resize(Window, 820, 540, center: true);
    }

    private static NumberBox TimePart(string label, int value, int maximum) => new()
    {
        Header = label, Value = value, Minimum = 0, Maximum = maximum, SmallChange = 1, LargeChange = 5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 83, MaxWidth = 115,
        ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten
    };

    private static void AddShortcut(UIElement target, VirtualKey key, Action invoke)
    {
        var shortcut = new KeyboardAccelerator { Key = key };
        shortcut.Invoked += (_, args) => { invoke(); args.Handled = true; };
        target.KeyboardAccelerators.Add(shortcut);
    }

    private TimeSpan ReadDuration() => TimeSpan.FromSeconds(((Part(hours, 99) * 60) + Part(minutes, 59)) * 60 + Part(seconds, 59));

    private static int Part(NumberBox input, int maximum) =>
        double.IsFinite(input.Value) ? (int)Math.Clamp(Math.Round(input.Value), 0, maximum) : 0;

    private void ToggleTimer()
    {
        bool starting = countdown.State is CountdownState.Ready or CountdownState.Finished;
        var remaining = countdown.Toggle(ReadDuration());
        if (starting)
        {
            alarm.Stop();
            SetDurationFields(countdown.Duration);
        }
        ApplyState(remaining);
    }

    private void Tick()
    {
        if (countdown.State != CountdownState.Running) return;
        var remaining = countdown.Tick();
        if (countdown.State == CountdownState.Finished) ApplyState(remaining);
        else ShowRemaining(remaining);
    }

    private void PlayAlarm()
    {
        try
        {
            alarm.Stream ??= Properties.Resources.TimerDownNotice;
            alarm.Play();
        }
        catch (Exception)
        {
            WinUiClassroomTools.Announce(status, UiText.Get("Actions.TimeUpAlarmFailed"));
        }
        accessibility.Reveal(status);
    }

    private void Reset()
    {
        alarm.Stop();
        ApplyState(countdown.Reset(ReadDuration()));
    }

    private void SetDurationFields(TimeSpan value)
    {
        hours.Value = (int)value.TotalHours;
        minutes.Value = value.Minutes;
        seconds.Value = value.Seconds;
    }

    private void ShowRemaining(TimeSpan value, bool updateProgress = true)
    {
        int total = (int)Math.Max(0, Math.Ceiling(value.TotalSeconds));
        string displayText = $"{total / 3_600:00}:{total / 60 % 60:00}:{total % 60:00}";
        if (time.Text != displayText)
        {
            time.Text = displayText;
            AutomationProperties.SetName(time, UiText.Format("Actions.TimeRemainingSpoken", total / 3_600, total / 60 % 60, total % 60));
        }
        double progressSeconds = accessibility.AnimationsEnabled ? value.TotalSeconds : total;
        progress.Value = updateProgress ? Math.Clamp(progressSeconds / countdown.Duration.TotalSeconds, 0, 1) : 1;
    }

    private void ApplyState(TimeSpan remaining)
    {
        var state = countdown.State;
        ShowRemaining(remaining, updateProgress: state != CountdownState.Ready);
        start.Content = state switch { CountdownState.Running => UiText.Get("Actions.Pause"), CountdownState.Paused => UiText.Get("Actions.Resume"), _ => UiText.Get("Actions.Start") };
        progress.ShowPaused = state == CountdownState.Paused;
        hours.IsEnabled = minutes.IsEnabled = seconds.IsEnabled = state is CountdownState.Ready or CountdownState.Finished;
        string message = state switch
        {
            CountdownState.Running => UiText.Format("Actions.CountdownEndsAt", DateTimeOffset.Now.Add(remaining)),
            CountdownState.Paused => UiText.Get("Actions.Paused"),
            CountdownState.Finished => UiText.Get("Actions.TimeUp"),
            _ => UiText.Get("Actions.Ready")
        };
        WinUiClassroomTools.Announce(status, message);
        if (state == CountdownState.Running) timer.Start();
        else timer.Stop();
        if (state == CountdownState.Finished) PlayAlarm();
    }

    private void SetCompact(bool value)
    {
        if (value && fullScreen) SetFullScreen(false);
        compact = value;
        heading.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        fullScreenButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        durationControls.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        compactButton.Content = value ? UiText.Get("Actions.Expand") : UiText.Get("Actions.Compact");
        panel.Spacing = value ? 10 : 18;
        panel.Padding = value ? new Thickness(12, 4, 12, 9) : new Thickness(28, 18, 28, 18);
        display.Padding = value ? new Thickness(14, 8, 14, 8) : new Thickness(30, 26, 30, 24);
        time.FontSize = value ? 49 : 112;
        WinUiClassroomTools.Resize(Window, value ? 400 : 820, value ? 274 : 540, center: !value);
    }

    private void SetFullScreen(bool value)
    {
        if (value && compact) SetCompact(false);
        Window.AppWindow.SetPresenter(value ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        fullScreen = value;
        fullScreenButton.Content = value ? UiText.Get("Actions.ExitFullScreen") : UiText.Get("Actions.FullScreen");
        time.FontSize = value ? 180 : 112;
        if (!value) WinUiClassroomTools.SetAlwaysOnTop(Window);
    }
}
