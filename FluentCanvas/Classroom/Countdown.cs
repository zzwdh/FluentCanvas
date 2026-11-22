using System;

namespace FluentCanvas.Classroom;

internal enum CountdownState { Ready, Running, Paused, Finished }

internal sealed class Countdown(TimeProvider clock)
{
    private long startedAt;
    private TimeSpan remainingAtStart;

    internal CountdownState State { get; private set; }
    internal TimeSpan Duration { get; private set; }

    internal TimeSpan Toggle(TimeSpan selectedDuration)
    {
        if (State == CountdownState.Running)
        {
            remainingAtStart = Tick();
            if (State == CountdownState.Running) State = CountdownState.Paused;
            return remainingAtStart;
        }

        if (State != CountdownState.Paused)
        {
            Duration = selectedDuration > TimeSpan.Zero ? selectedDuration : TimeSpan.FromSeconds(1);
            remainingAtStart = Duration;
        }
        startedAt = clock.GetTimestamp();
        State = CountdownState.Running;
        return remainingAtStart;
    }

    internal TimeSpan Reset(TimeSpan selectedDuration)
    {
        if (State == CountdownState.Running)
        {
            startedAt = clock.GetTimestamp();
            return remainingAtStart = Duration;
        }
        State = CountdownState.Ready;
        return remainingAtStart = selectedDuration;
    }

    internal TimeSpan Tick()
    {
        if (State != CountdownState.Running) return remainingAtStart;
        var remaining = remainingAtStart - clock.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero) return remaining;
        State = CountdownState.Finished;
        return remainingAtStart = TimeSpan.Zero;
    }
}
