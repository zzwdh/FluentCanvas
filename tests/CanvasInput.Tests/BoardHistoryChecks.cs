using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using FluentCanvas;
using FluentCanvas.Helpers;

internal static class BoardHistoryChecks
{
    internal static void Run()
    {
        VerifyGestureReplay();
        VerifyReplacementErasureAndReset();
        Console.WriteLine("PASS Board gesture snapshots group undo, preserve points across replacement and erasure, and clear on reset");
    }

    private static void VerifyGestureReplay()
    {
        var window = new MainWindow();
        try
        {
            var stroke = MakeStroke(0);
            var before = stroke.StylusPoints.Clone();
            window.Canvas.Strokes.Add(stroke);
            var element = new Image { Name = "InsertedImage", RenderTransform = new TransformGroup() };
            window.Canvas.Children.Add(element);
            int historyStart = window.History.CaptureState().Entries.Length;
            window.History.MarkSaved();

            window.BeginBoardGesture();
            window.TrackBoardTransform(stroke, Translation(5));
            window.TrackBoardTransform(stroke, Translation(5));
            window.ApplyElementMatrixTransform(element, Translation(10));
            var after = stroke.StylusPoints.Clone();
            Check(after[0].X == before[0].X + 10 && after[0].PressureFactor == before[0].PressureFactor,
                "Visible gesture transforms must keep the pressure samples.");
            Check(window.HasPendingPointHistory && !window.History.HasUnsavedChanges,
                "The pending gesture should be observable to the dirty check before the history commit.");

            window.FinishBoardGesture();
            Check(window.History.CaptureState().Entries.Length == historyStart + 1 && !window.HasPendingPointHistory,
                "The completed gesture should be represented by a single commit for the ink and element.");
            window.Undo();
            Check(stroke.StylusPoints.SequenceEqual(before) && element.RenderTransform.Value.IsIdentity && !window.History.HasUnsavedChanges,
                "One undo should restore the ink, element, and saved state.");
            window.Redo();
            Check(stroke.StylusPoints.SequenceEqual(after) && element.RenderTransform.Value.OffsetX == 10,
                "Redo should restore the completed gesture.");

            // Ordinary point edits still update history without waiting for a board gesture.
            stroke.Transform(Translation(4), false);
            Check(window.History.CaptureState().Entries.Length == historyStart + 2 && !window.HasPendingPointHistory,
                "A normal transformation should commit as soon as all affected strokes are updated.");
            window.Undo();
            Check(stroke.StylusPoints.SequenceEqual(after), "A later edit must not mutate the previous gesture snapshot.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void VerifyReplacementErasureAndReset()
    {
        var window = new MainWindow();
        try
        {
            var pressureStroke = MakeStroke(10);
            var erasedStroke = MakeStroke(40);
            window.Canvas.Strokes.Add(new StrokeCollection { pressureStroke, erasedStroke });
            window.BeginBoardGesture();
            window.TrackBoardTransform(pressureStroke, Translation(12));
            window.TrackBoardTransform(erasedStroke, Translation(12));
            var beforePressureReplacement = pressureStroke.StylusPoints.Clone();
            var beforeErasure = erasedStroke.StylusPoints.Clone();

            pressureStroke.StylusPoints = MakeStroke(90).StylusPoints;
            window.Canvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
            window.Canvas.Strokes.Remove(erasedStroke);
            erasedStroke.Transform(Translation(18), false);
            window.FinishBoardGesture();

            var entries = window.History.CaptureState().Entries;
            var saved = entries[^1].StylusPointDictionary;
            Check(entries[^2].CommitType == TimeMachineHistoryType.Clear &&
                  saved[pressureStroke].Item2.SequenceEqual(beforePressureReplacement) &&
                  saved[erasedStroke].Item2.SequenceEqual(beforeErasure),
                "An interleaved replacement or erase should keep the same final point snapshot as eager history.");

            window.BeginBoardGesture();
            window.TrackBoardTransform(pressureStroke, Translation(4));
            Check(window.HasPendingPointHistory, "The second gesture should have an uncommitted snapshot.");
            window.AbortBoardGesture();
            window.Canvas.EditingMode = InkCanvasEditingMode.Ink;
            window.Canvas.Strokes.Add(MakeStroke(130));
            window.FinishBoardGesture();
            Check(!window.HasPendingPointHistory && window.History.CaptureState().Entries.Length == 1,
                "An aborted gesture cannot contribute an old-page snapshot after the canvas resets.");
        }
        finally
        {
            window.Close();
        }
    }

    internal static void Measure()
    {
        const int StrokeCount = 450, PointCount = 96, Deltas = 24;
        MeasureBoard(20, PointCount, 2, defer: false);
        MeasureBoard(20, PointCount, 2, defer: true);

        var eager = MeasureBoard(StrokeCount, PointCount, Deltas, defer: false);
        var deferred = MeasureBoard(StrokeCount, PointCount, Deltas, defer: true);
        Console.WriteLine($"{StrokeCount} strokes × {PointCount} points × {Deltas} board deltas plus history commit");
        Console.WriteLine($"Eager snapshots: {eager.Bytes:N0} managed bytes, {eager.Milliseconds:F1} ms.");
        Console.WriteLine($"Deferred snapshots: {deferred.Bytes:N0} managed bytes, {deferred.Milliseconds:F1} ms.");
    }

    private static (long Bytes, double Milliseconds) MeasureBoard(int strokeCount, int pointCount, int deltas, bool defer)
    {
        var window = new MainWindow();
        try
        {
            var strokes = Enumerable.Range(0, strokeCount).Select(i => MakeStroke(i, pointCount)).ToArray();
            window.Canvas.Strokes.Add(new StrokeCollection(strokes));
            window.BeginBoardGesture();
            var transform = Translation(0.25);
            long startingBytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < deltas; i++)
            {
                foreach (var stroke in strokes)
                {
                    if (defer) window.TrackBoardTransform(stroke, transform);
                    else stroke.Transform(transform, false);
                }
            }
            window.FinishBoardGesture();
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return (GC.GetAllocatedBytesForCurrentThread() - startingBytes, elapsed);
        }
        finally
        {
            window.Close();
        }
    }

    private static Stroke MakeStroke(int offset, int count = 4)
    {
        var points = new StylusPointCollection(count);
        for (int i = 0; i < count; i++)
            points.Add(new StylusPoint(offset + i, i * 2, (i % 5 + 1) * 0.125f));
        return new Stroke(points);
    }

    private static Matrix Translation(double distance) => new(1, 0, 0, 1, distance, 0);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
