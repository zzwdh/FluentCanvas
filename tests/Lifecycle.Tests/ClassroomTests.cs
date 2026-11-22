using FluentCanvas.Classroom;

internal static class ClassroomTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var clock = new TestClock();
        var countdown = new Countdown(clock);
        var minute = TimeSpan.FromMinutes(1);
        check(countdown.Toggle(minute) == minute && countdown.State == CountdownState.Running, "Countdown starts at the selected duration");
        clock.Advance(TimeSpan.FromSeconds(18));
        var paused = countdown.Toggle(minute);
        clock.Advance(minute);
        check(countdown.Tick() == paused && paused == TimeSpan.FromSeconds(42), "Paused countdown excludes wall time");
        check(countdown.Toggle(minute) == paused && countdown.State == CountdownState.Running, "Resume keeps the remaining duration");
        check(countdown.Reset(TimeSpan.Zero) == minute && countdown.State == CountdownState.Running, "Reset during a run restarts the original duration");
        clock.Advance(minute);
        check(countdown.Toggle(minute) == TimeSpan.Zero && countdown.State == CountdownState.Finished, "Pause at the deadline completes the countdown");
        check(countdown.Reset(TimeSpan.Zero) == TimeSpan.Zero && countdown.State == CountdownState.Ready, "Finished countdown resets to the current entry");
        check(countdown.Toggle(TimeSpan.Zero) == TimeSpan.FromSeconds(1), "Starting a zero duration grants one second");

        string directory = Path.Combine(root, "roster");
        var store = new RosterStore(directory);
        var empty = store.Read();
        var numbers = empty.Draw(15, 15, new Random(7));
        check(numbers.Select(int.Parse).Order().SequenceEqual(Enumerable.Range(1, 15)), "Numeric draw visits every participant exactly once");
        var saved = store.Save(empty, "A\r\nB\r\nSkip\r\nA\r\n", "A --> B\nB --> C\nSkip -->");
        check(store.Read().Names.SequenceEqual(new[] { "C", "C", "C" }), "Legacy chained rules, blank replacements and duplicate names survive save and reload");
        bool conflict = false;
        try { store.Save(empty, "Other", ""); } catch (IOException) { conflict = true; }
        check(conflict && store.Read().NamesText == saved.NamesText, "Stale editor cannot overwrite the last saved roster");
        bool invalid = false;
        try { store.Save(saved, "", "no arrow"); } catch (InvalidDataException) { invalid = true; }
        check(invalid && store.Read().ReplacementsText == saved.ReplacementsText, "Rejected rules preserve the previous files");
        bool failed = false;
        using (File.Open(Path.Combine(directory, "Names.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { store.Save(saved, "Changed", "Changed --> New"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
        }
        var restored = store.Read();
        check(failed && restored.NamesText == saved.NamesText && restored.ReplacementsText == saved.ReplacementsText,
            "Failure to replace the names restores the previous rules");
    }

    private sealed class TestClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }
}
