using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using FluentCanvas.Helpers;

internal static class SparseInkTests
{
    internal static void Run()
    {
        VerifyDisjointEraserAndRollback();
        foreach (int count in new[] { 100, 1_000, 10_000 })
        {
            var canvas = new InkCanvas();
            var history = new TimeMachine();
            history.BindInk(canvas.Strokes);
            var layers = new StrokeCollection(Enumerable.Range(0, count).Select(i =>
                new Stroke(new StylusPointCollection { new StylusPoint(i, 0), new StylusPoint(i, 12) })));
            canvas.Strokes.Add(layers);
            history.CommitStrokeUserInputHistory(layers);
            var middle = canvas.Strokes[count / 2];
            var originalPoints = middle.StylusPoints;
            double[] durations = new double[21];
            long allocation = 0;
            for (int sample = -1; sample < durations.Length; sample++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                middle.DrawingAttributes.Color = Colors.Red;
                history.CommitStrokeDrawingAttributesHistory(new[] { middle });
                history.Undo().ProjectInk(canvas);
                if (middle.DrawingAttributes.Color != Colors.Black || !ReferenceEquals(originalPoints, middle.StylusPoints))
                    throw new Exception("A sparse brush undo replaced the point collection or lost its original color.");
                history.Redo().ProjectInk(canvas);
                canvas.Strokes.RemoveAt(count / 2);
                history.CommitStrokeEraseHistory(new StrokeCollection { middle });
                history.Undo().ApplyClear(canvas);
                if (!ReferenceEquals(canvas.Strokes[count / 2], middle) || middle.DrawingAttributes.Color != Colors.Red)
                    throw new Exception("Single-layer erasure restored the wrong brush or layer.");
                if (sample >= 0)
                {
                    durations[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    allocation += GC.GetAllocatedBytesForCurrentThread() - before;
                }
                history.Undo().ProjectInk(canvas);
                originalPoints = middle.StylusPoints;
            }
            Array.Sort(durations);
            Console.WriteLine($"Sparse ink, {count} layers: P95 {durations[19]:F3} ms, mean managed allocation {allocation / durations.Length:N0} bytes (brush change/undo/redo, middle erase/undo, 21 samples).");
        }
    }
    private static void VerifyDisjointEraserAndRollback()
    {
        var canvas = new InkCanvas();
        var history = new TimeMachine();
        history.BindInk(canvas.Strokes);
        Stroke Make(int i) => new(new StylusPointCollection { new StylusPoint(i, 0), new StylusPoint(i, 1) });
        var originals = Enumerable.Range(0, 10).Select(Make).ToArray();
        var added = new[] { Make(31), Make(32), Make(33), Make(34) };
        canvas.Strokes.Add(new StrokeCollection(originals));
        history.CommitStrokeUserInputHistory(new StrokeCollection(originals));
        var snapshot = history.CaptureState();

        canvas.Strokes.Replace(originals[7], new StrokeCollection(added[..2]));
        canvas.Strokes.Replace(originals[2], new StrokeCollection(added[2..]));
        var expected = canvas.Strokes.ToArray();
        history.CommitStrokeEraseHistory(new StrokeCollection { originals[7], originals[2] }, new StrokeCollection(added));
        ulong revision = history.CurrentState;
        void Fail(bool _) => throw new ApplicationException("Test a failed projection notification");
        history.OnUndoStateChanged += Fail;
        try { history.Undo(); throw new Exception("A failing cursor notification should propagate."); }
        catch (ApplicationException) { }
        finally { history.OnUndoStateChanged -= Fail; }
        if (history.CurrentState != revision || !canvas.Strokes.SequenceEqual(expected))
            throw new Exception("Failed undo moved the native cursor.");
        history.Undo().ApplyClear(canvas);
        if (!canvas.Strokes.SequenceEqual(originals)) throw new Exception("Undo lost disjoint layer order.");
        history.Redo().ApplyClear(canvas);
        if (!canvas.Strokes.SequenceEqual(expected)) throw new Exception("Wrong order after replaying disjoint fragments.");
        var retained = originals[3].StylusPoints;
        originals[2].DrawingAttributes.Color = Colors.Purple;
        var restored = new StrokeCollection();
        InkProjection.Restore(restored, snapshot.Page, originals.ToDictionary(NativeInkPage.Id), changed: null);
        if (!restored.SequenceEqual(originals) || !ReferenceEquals(restored[3].StylusPoints, retained) ||
            restored[2].DrawingAttributes.Color != Colors.Black)
            throw new Exception("Page restore did not distinguish reusable and externally changed visuals.");
    }
}
