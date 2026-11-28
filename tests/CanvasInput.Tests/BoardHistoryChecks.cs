using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentCanvas;
using FluentCanvas.Helpers;

internal static class BoardHistoryChecks
{
    internal static void Run()
    {
        VerifyNativeProjection();
        VerifyGestureReplay();
        VerifyFrozenGesture();
        VerifyRasterGesture();
        VerifyReplacementErasureAndReset();
        MixedSceneChecks.Run();
        Console.WriteLine("PASS Board gesture snapshots group undo, preserve points across replacement and erasure, and clear on reset");
    }

    private static void VerifyNativeProjection()
    {
        var window = new MainWindow();
        try
        {
            var first = new Stroke(new StylusPointCollection(Enumerable.Range(0, 8).Select(i => new StylusPoint(i + 0.125, i))));
            var second = MakeStroke(12);
            var third = MakeStroke(20);
            window.Canvas.Strokes.Add(new StrokeCollection { first, second, third });
            window.FinishCollectedStroke(first, 0, 40);
            var pressure = first.StylusPoints.Clone();
            Check(pressure.Any(point => point.PressureFactor < 0.4f), "Native pen finishing should taper constant-pressure ink.");
            var native = window.History.MaterializeInk();
            Check(native[0].StylusPoints.SequenceEqual(pressure) && native[0] != first,
                "The committed native records must contain independent, pressure-finished samples.");
            Check(window.History.CaptureState().Entries.Length == 1,
                "Finishing pressure must not add an undo step.");

            window.Canvas.Strokes.Remove(first);
            first.StylusPoints = MakeStroke(99).StylusPoints;
            window.Undo();
            Check(window.Canvas.Strokes.SequenceEqual(new[] { first, second, third })
                  && first.StylusPoints.SequenceEqual(pressure), "Undo restores Rust geometry and layer order.");
            window.Redo();
            Check(window.Canvas.Strokes.SequenceEqual(new[] { second, third }), "Redo removes only the erased layer.");
            window.Undo();
            window.Undo();
            window.Redo();
            Check(first.StylusPoints.SequenceEqual(pressure), "Redo preserves the finished pen pressure.");

            var originalColor = second.DrawingAttributes.Color;
            second.DrawingAttributes.Color = Colors.Crimson;
            window.FinishBoardGesture();
            Check(window.History.MaterializeInk()[1].DrawingAttributes.Color == Colors.Crimson,
                "The native record must contain the updated brush.");
            window.Undo();
            Check(second.DrawingAttributes.Color == originalColor, "Undo must restore the previous brush.");
            window.Redo();
            Check(second.DrawingAttributes.Color == Colors.Crimson, "Redo must restore the committed brush.");
            window.Canvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
            var fragment = MakeStroke(44);
            var fragmentA = MakeStroke(46);
            var fragmentB = MakeStroke(48);
            window.Canvas.Strokes.Replace(second, new StrokeCollection { fragment });
            window.Canvas.Strokes.Replace(fragment, new StrokeCollection { fragmentA, fragmentB });
            window.FinishBoardGesture();
            var split = window.Canvas.Strokes.ToArray();
            window.Undo();
            Check(window.Canvas.Strokes.SequenceEqual(new[] { first, second, third }),
                "Repeated point erasures in one gesture must restore the original stroke only.");
            window.Redo();
            Check(window.Canvas.Strokes.SequenceEqual(split) && !window.Canvas.Strokes.Contains(fragment),
                "Redo must retain final fragments and their original indices.");
        }
        finally { window.Close(); }
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
            var replacement = MakeStroke(90).StylusPoints;
            var beforeErasure = erasedStroke.StylusPoints.Clone();

            pressureStroke.StylusPoints = replacement;
            window.Canvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
            window.Canvas.Strokes.Remove(erasedStroke);
            erasedStroke.Transform(Translation(18), false);
            window.FinishBoardGesture();

            window.Undo();
            Check(window.Canvas.Strokes.SequenceEqual(new[] { pressureStroke, erasedStroke }) &&
                  pressureStroke.StylusPoints.SequenceEqual(replacement) &&
                  erasedStroke.StylusPoints.SequenceEqual(beforeErasure),
                "Undo of erasure must recover the geometry at the start of the erase.");
            window.Undo();
            Check(pressureStroke.StylusPoints.SequenceEqual(MakeStroke(10).StylusPoints) &&
                  erasedStroke.StylusPoints.SequenceEqual(MakeStroke(40).StylusPoints),
                "Undo of the preceding gesture must restore both original trajectories.");
            window.Redo();
            window.Redo();
            Check(window.Canvas.Strokes.Single() == pressureStroke && pressureStroke.StylusPoints.SequenceEqual(replacement),
                "Redo must reproduce the actual final geometry and erasure.");
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

    private static void VerifyFrozenGesture()
    {
        var window = new MainWindow();
        try
        {
            var first = MakeStroke(10);
            var other = MakeStroke(50);
            first.DrawingAttributes.Width = DrawingAttributes.MaxWidth / 2;
            var canvas = window.Canvas;
            canvas.Strokes.Add(new StrokeCollection { first, other });
            var media = new Image { Name = "SelectedImage", Width = 18, Height = 12, RenderTransformOrigin = new(0.5, 0.5) };
            InkCanvas.SetLeft(media, 30);
            InkCanvas.SetTop(media, 12);
            canvas.Children.Add(media);
            var untouched = new Border { Name = "OtherImage", Width = 12, Height = 12 };
            canvas.Children.Add(untouched);
            window.Show();
            canvas.UpdateLayout();
            var at = media.TranslatePoint(new(0, 0), canvas);
            var before = first.StylusPoints.Clone();
            var id = window.History.CurrentState;
            var move = Translation(12);
            window.BeginBoardGesture();
            window.PreviewSelection([first], [media], move);
            canvas.UpdateLayout();
            Check(first.StylusPoints.SequenceEqual(before) && window.History.CurrentState == id,
                "Preview must leave both authoritative and WPF input geometry frozen.");
            Check(media.TranslatePoint(new(0, 0), canvas).X == at.X + 12 && untouched.RenderTransform.Value.IsIdentity,
                $"Only the targeted media should move while previewing. Before {at}, preview {media.TranslatePoint(new(0,0),canvas)}, transform {media.RenderTransform.Value}.");
            window.FinishBoardGesture();
            canvas.UpdateLayout();
            Check(first.StylusPoints[0].X == before[0].X + 12 && other.StylusPoints[0].X == 50,
                "Selection commit moved an unrelated stroke.");
            Check(media.TranslatePoint(new(0, 0), canvas).X == at.X + 12 && !window.HasGesturePreview,
                "Finishing a preview must keep the same media position.");
            window.Undo();
            canvas.UpdateLayout();
            Check(first.StylusPoints.SequenceEqual(before) && media.TranslatePoint(new(0, 0), canvas) == at,
                "Ink and media undo must share one revision.");
            window.Redo();
            window.Undo();
            canvas.UpdateLayout();

            window.BeginBoardGesture();
            var up = Matrix.Identity;
            up.ScaleAt(4, 2, 100, 75);
            var down = Matrix.Identity;
            down.ScaleAt(0.5, 0.5, 100, 75);
            window.PreviewBoard(up, 4, 2);
            window.PreviewBoard(down, 0.5, 0.5);
            var expected = up;
            expected.Append(down);
            canvas.UpdateLayout();
            var previewPoint = media.TranslatePoint(new(0, 0), canvas);
            Check((previewPoint - expected.Transform(at)).Length < 0.0001,
                "A media element with a layout offset and centered origin used the wrong pivot.");
            window.FinishBoardGesture();
            canvas.UpdateLayout();
            Check((media.TranslatePoint(new(0, 0), canvas) - previewPoint).Length < 0.0001 &&
                  first.DrawingAttributes.Width == DrawingAttributes.MaxWidth / 2 &&
                  first.StylusPoints[0].ToPoint() == expected.Transform(before[0].ToPoint()),
                "Final projection must preserve the pivot and intermediate brush width limits.");
            window.Undo();
            canvas.UpdateLayout();
            window.BeginBoardGesture();
            window.PreviewBoard(move);
            window.CancelPreview();
            window.FinishBoardGesture();
            canvas.UpdateLayout();
            Check(window.History.CurrentState == id && first.StylusPoints.SequenceEqual(before) &&
                media.TranslatePoint(new(0, 0), canvas) == at, "Cancelling a gesture changed the ink or media.");
        }
        finally { window.Close(); }
    }

    private static void VerifyRasterGesture()
    {
        var window = new MainWindow();
        try
        {
            var canvas = window.Canvas;
            Stroke Line(int x, Color color) => new(new StylusPointCollection
            {
                new StylusPoint(x, 36), new StylusPoint(x + 22, 36)
            }, new DrawingAttributes { Width = 10, Height = 10, Color = color, IgnorePressure = true });

            var red = Line(10, Colors.Red);
            var blue = Line(140, Colors.Blue);
            canvas.Strokes.Add(new StrokeCollection { red, blue });
            var media = new Border { Width = 12, Height = 12, Background = Brushes.Lime };
            InkCanvas.SetLeft(media, 10);
            InkCanvas.SetTop(media, 60);
            canvas.Children.Add(media);
            var stationary = new Border { Width = 12, Height = 12, Background = Brushes.Gold };
            InkCanvas.SetLeft(stationary, 140);
            InkCanvas.SetTop(stationary, 60);
            canvas.Children.Add(stationary);
            window.Show();

            Color Pixel(int x, int y)
            {
                canvas.UpdateLayout();
                var bitmap = new RenderTargetBitmap(240, 150, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(canvas);
                var pixel = new byte[4];
                bitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
                return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
            }
            Check(Pixel(20, 36) == Colors.Red && Pixel(150, 36) == Colors.Blue, "Ink must render before preview.");
            window.BeginBoardGesture();
            window.PreviewSelection([red], [media], Translation(60));
            Check(Pixel(20, 36).A == 0 && Pixel(80, 36) == Colors.Red && Pixel(150, 36) == Colors.Blue,
                "Frozen ink preview must move selected ink once and preserve stationary ink.");
            Check(Pixel(74, 64) == Colors.Lime && Pixel(144, 64) == Colors.Gold,
                "Live media and unselected children must retain their own preview positions.");
            window.FinishBoardGesture();
            Check(Pixel(80, 36) == Colors.Red && Pixel(150, 36) == Colors.Blue,
                "The final native projection must match the preview pixels.");
            window.Undo();
            Check(Pixel(20, 36) == Colors.Red && Pixel(14, 64) == Colors.Lime,
                "Undo must restore ink and unnamed live media without using a name lookup.");
        }
        finally { window.Close(); }
    }
    internal static void Measure()
    {
        const int runs = 7, strokes = 450, points = 96, deltas = 24;
        _ = MeasureBoard(24, points, 3, preview: false);
        _ = MeasureBoard(24, points, 3, preview: true);
        var eager = new List<Measurement>();
        var deferred = new List<Measurement>();
        for (int i = 0; i < runs; i++)
        {
            bool firstIsPreview = i % 2 != 0;
            foreach (bool preview in new[] { firstIsPreview, !firstIsPreview })
                (preview ? deferred : eager).Add(MeasureBoard(strokes, points, deltas, preview));
        }
        Console.WriteLine($"{strokes} strokes × {points} points × {deltas} deltas, {runs} alternating runs per path.");
        Print("WPF eager point transforms + native history", eager);
        Print("Frozen visual preview + native batch transform", deferred);

        static double Percentile(IEnumerable<double> values, double fraction)
        {
            var sorted = values.Order().ToArray();
            return sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
        }

        static void Print(string path, List<Measurement> samples) =>
            Console.WriteLine($"{path}: total median {Percentile(samples.Select(s => s.Total), .5):F2} ms; " +
                $"first delta median {Percentile(samples.Select(s => s.First), .5):F2} ms; " +
                $"later delta p95 {1000*Percentile(samples.SelectMany(s => s.Later), .95):F2} microseconds; " +
                $"commit median {Percentile(samples.Select(s => s.Commit), .5):F2} ms; " +
                $"commit p95 {Percentile(samples.Select(s => s.Commit), .95):F2} ms; " +
                $"post-commit presenter handoff median {Percentile(samples.Select(s => s.Handoff), .5):F2} ms; " +
                $"max post-commit rendering callback span {samples.Max(s => s.MaxFrame):F2} ms; " +
                $"handoff incl. dispatcher mean allocations {samples.Average(s => s.HandoffBytes):N0} B; " +
                $"measuring-thread managed allocations median {Percentile(samples.Select(s => (double)s.Bytes), .5):N0} B.");
    }

    private sealed record Measurement(long Bytes, double Total, double First, double[] Later, double Commit,
        double Handoff,double MaxFrame,long HandoffBytes);

    private static Measurement MeasureBoard(int strokeCount, int pointCount, int deltas, bool preview)
    {
        var window = new MainWindow();
        try
        {
            var strokes = Enumerable.Range(0, strokeCount).Select(i => MakeStroke(i, pointCount)).ToArray();
            window.Canvas.Strokes.Add(new StrokeCollection(strokes));
            window.Show();
            window.Canvas.UpdateLayout();
            window.BeginBoardGesture();
            var matrix = Translation(0.25);
            var durations = new double[deltas];
            long frameStart = 0;
            double maxFrame = 0;
            void StartFrame(object sender, EventArgs args) => frameStart = Stopwatch.GetTimestamp();
            void EndFrame(object sender, EventArgs args)
            {
                if (frameStart != 0)
                    maxFrame = Math.Max(maxFrame,Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds);
            }
            CompositionTarget.Rendering += StartFrame;
            long startingBytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < deltas; i++)
            {
                long update = Stopwatch.GetTimestamp();
                if (preview) window.PreviewBoard(matrix);
                else foreach (var stroke in strokes) window.TrackBoardTransform(stroke, matrix);
                durations[i] = Stopwatch.GetElapsedTime(update).TotalMilliseconds;
            }
            long finish = Stopwatch.GetTimestamp();
            window.FinishBoardGesture();
            long end = Stopwatch.GetTimestamp();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - startingBytes;
            double handoff = 0;
            long handoffBytes = 0;
            if (window.HasCommittedPreview)
            {
                long startBytes = GC.GetAllocatedBytesForCurrentThread();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(2) };
                timer.Tick += (_,_) =>
                {
                    if (!window.HasCommittedPreview || Stopwatch.GetElapsedTime(end).TotalSeconds > 4) frame.Continue = false;
                };
                timer.Start();
                CompositionTarget.Rendering += EndFrame;
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); CompositionTarget.Rendering -= EndFrame; }
                Check(!window.HasCommittedPreview,"WPF presenter did not take over the committed preview.");
                handoff=Stopwatch.GetElapsedTime(end).TotalMilliseconds;
                handoffBytes=GC.GetAllocatedBytesForCurrentThread()-startBytes;
            }
            CompositionTarget.Rendering -= StartFrame;
            Check(strokes[^1].StylusPoints[0].X == strokeCount - 1 + 0.25 * deltas,
                "Both measurement paths must perform the same transformation.");
            return new(bytes, Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, durations[0], durations[1..],
                Stopwatch.GetElapsedTime(finish, end).TotalMilliseconds,handoff,maxFrame,handoffBytes);
        }
        finally { window.Close(); }
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
