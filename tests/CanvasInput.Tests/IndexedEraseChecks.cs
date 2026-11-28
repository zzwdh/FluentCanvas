using System.Windows;
using System.Windows.Ink;
using System.Windows.Controls;
using System.Windows.Input;
using FluentCanvas;
using FluentCanvas.Helpers;

internal static class IndexedEraseChecks
{
    internal static void Measure()
    {
        using var page = NativeInkPage.Create();
        var strokes = new StrokeCollection();
        for (int j = 0; j < 100; j++)
            for (int i = 0; i < 100; i++)
            {
                var start = new Point(32*i, 32*j);
                strokes.Add(new Stroke(new StylusPointCollection([start, start + new Vector(16,0)])));
            }
        page.Append(strokes);
        var results = new NativeInkPage.Cut[16];
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        page.QueryErase(new Point(8,-2), new Point(8,2), 2, ref results);
        var cold = System.Diagnostics.Stopwatch.GetElapsedTime(t);
        var samples = new double[401];
        for (int i = 0; i < samples.Length; i++)
        {
            double x = 8 + (i * 61 % 100) * 32, y = (i * 37 % 100) * 32;
            t = System.Diagnostics.Stopwatch.GetTimestamp();
            int count = page.QueryErase(new Point(x,y-4), new Point(x,y+4), 2, ref results);
            samples[i] = System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (count != 1) throw new Exception("Sparse eraser query returned an incorrect candidate.");
        }
        Array.Sort(samples);
        Console.WriteLine($"Indexed hit test, 10,000 strokes (100 x 100 evenly spaced two-point lines), cold {cold.TotalMilliseconds:F3} ms, 401 warm queries P50 {samples[200]:F2} us, P95 {samples[380]:F2} us. Includes native geometry query only.");
    }

    internal static void Run()
    {
        var window = new MainWindow();
        try
        {
            var description = new StylusPointDescription([
                new StylusPointPropertyInfo(StylusPointProperties.X),
                new StylusPointPropertyInfo(StylusPointProperties.Y),
                new StylusPointPropertyInfo(StylusPointProperties.NormalPressure),
                new StylusPointPropertyInfo(StylusPointProperties.XTiltOrientation)
            ]);
            var line = new Stroke(new StylusPointCollection(description) {
                new StylusPoint(0, 20, .25f, description, [0]),
                new StylusPoint(100, 20, .75f, description, [60])
            }, new DrawingAttributes { Width = 2, Height = 2, FitToCurve = false });
            var untouched = new Stroke(new StylusPointCollection([new Point(0, 200), new Point(100, 200)]));
            var shape = ParametricInk.Ellipse(new Point(10, 10), new Point(90, 72)).Create(line.DrawingAttributes);
            window.Canvas.Strokes.Add(new StrokeCollection { line, untouched, shape });
            window.BeginIndexedErase(4, false);
            window.AdvanceIndexedErase(new Point(50, -3));
            window.AdvanceIndexedErase(new Point(50, 90));
            window.AdvanceIndexedErase(new Point(50, -3));
            window.FinishBoardGesture();
            var cut = window.Canvas.Strokes.ToArray();
            if (cut.Length != 6 || cut[2] != untouched || cut[0].StylusPoints[0].X != 0 ||
                cut[1].StylusPoints[^1].X != 100 || cut.Skip(3).Any(x => ParametricInk.Get(x) != null))
                throw new Exception("Native point erasure changed layer order or retained a whole-shape descriptor.");
            var tip = cut[0].StylusPoints[^1];
            if (tip.X < 46 || tip.X > 49 || Math.Abs(tip.PressureFactor - (.25 + tip.X * .005)) > .001 ||
                Math.Abs(tip.GetPropertyValue(StylusPointProperties.XTiltOrientation) - tip.X * .60) > .51)
                throw new Exception($"Split endpoint changed: X {tip.X}, pressure {tip.PressureFactor}, tilt {tip.GetPropertyValue(StylusPointProperties.XTiltOrientation)}.");
            window.Undo();
            if (!window.Canvas.Strokes.SequenceEqual(new[] { line, untouched, shape }) || ParametricInk.Get(shape) == null)
                throw new Exception("Undo of a whole eraser gesture failed to restore its parametric original.");
            window.Redo();
            if (!window.Canvas.Strokes.SequenceEqual(cut)) throw new Exception("Redo produced different fragments.");
            window.BeginIndexedErase(4, true);
            window.AdvanceIndexedErase(new Point(20,20));
            window.FinishBoardGesture();
            if (window.Canvas.Strokes.Contains(cut[0])) throw new Exception("Whole-stroke eraser missed the redo fragment.");
            window.Undo();
            if (!window.Canvas.Strokes.SequenceEqual(cut)) throw new Exception("Whole-stroke undo lost a layer.");
            window.InitializeIndexedEraser();
            window.Show();
            window.UpdateLayout();
            window.Canvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            window.Canvas.EraserShape = new EllipseStylusShape(8,8);
            var touch = new ManagedTouchDevice(window.Canvas, 78);
            ulong revision = window.History.CurrentState;
            touch.Down(20,20);
            if (window.History.CurrentState != revision || window.Canvas.Strokes.Contains(cut[0]))
                throw new Exception("Touch erasure must preview before committing the native revision.");
            touch.Move(23,20);
            touch.Up(23,20);
            if (window.Canvas.Strokes.Contains(cut[0]) || window.Canvas.AreAnyTouchesCapturedWithin)
                throw new Exception("Routed Windows touch erasure failed to remove ink or release capture.");
            window.Undo();
            if (!window.Canvas.Strokes.SequenceEqual(cut)) throw new Exception("Routed erasure did not commit one undo step.");
            var interrupted = new ManagedTouchDevice(window.Canvas, 79);
            interrupted.Down(20,20);
            interrupted.Capture(null);
            interrupted.Up(20,20);
            window.Undo();
            if (!window.Canvas.Strokes.SequenceEqual(cut)) throw new Exception("Capture loss left an eraser transaction open.");
            var switchTool = new ManagedTouchDevice(window.Canvas, 80);
            switchTool.Down(20,20);
            window.Canvas.EditingMode = InkCanvasEditingMode.Ink;
            if (window.Canvas.AreAnyTouchesCapturedWithin || window.History.CurrentState == revision)
                throw new Exception("Selecting another tool left an eraser transaction open.");
            switchTool.Up(20,20);
            var curved = new Stroke(new StylusPointCollection([new Point(10,120),new Point(40,88),
                new Point(70,145),new Point(100,130)]), new DrawingAttributes { Width = 9, Height = 3,
                FitToCurve = true, StylusTip = StylusTip.Rectangle });
            window.Canvas.Strokes.Add(curved);
            var curvePath = curved.GetBezierStylusPoints();
            var center = curvePath[curvePath.Count/2].ToPoint();
            var expected = curved.GetEraseResult(new[] { center }, new EllipseStylusShape(6,6));
            window.BeginIndexedErase(6,false);
            window.AdvanceIndexedErase(center);
            window.FinishBoardGesture();
            if (expected.Count != 2 || !window.Canvas.Strokes.TakeLast(2).Zip(expected)
                .All(pair => pair.First.StylusPoints.SequenceEqual(pair.Second.StylusPoints)))
                throw new Exception("A smoothed rectangular nib was not cut on its WPF outline.");
            window.Undo();
            if (!window.Canvas.Strokes.Contains(curved)) throw new Exception("The smoothed stroke was not restored.");
            Console.WriteLine("PASS Indexed erase, repeated cuts, routed touch, tool change, capture loss, smoothed nibs, shape fragments, layer order, pressure, tilt and undo/redo");
        }
        finally { window.Close(); }
    }
}
