using FluentCanvas;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ShapeDraftVisualTests
{
    internal static void Run()
    {
        var canvas = new ShapeDraftInkCanvas { Background = Brushes.Transparent };
        int builds = 0;
        Point endpoint = default;
        void Build(Point point) { builds++; endpoint = point; }
        for (int i = 0; i <= 200; i++) canvas.QueueGeometry(new Point(i, i), Build);
        if (builds != 0) throw new InvalidOperationException("Geometry was rebuilt for every input packet.");
        canvas.FlushQueuedGeometry();
        canvas.FlushQueuedGeometry();
        canvas.QueueGeometry(new Point(250, 250), Build);
        canvas.CancelQueuedGeometry();
        canvas.FlushQueuedGeometry();
        if (builds != 1 || endpoint != new Point(200, 200))
            throw new InvalidOperationException("Geometry flush or cancellation lost the last input position.");
        var bounds = new Size(64, 64);
        canvas.Measure(bounds);
        canvas.Arrange(new Rect(bounds));

        int changes = 0;
        canvas.Strokes.StrokesChanged += (_, _) => changes++;
        var first = new StrokeCollection { CreateLine(12) };
        var second = new StrokeCollection { CreateLine(36) };
        canvas.SetShapePreview(first, second);
        RenderTargetBitmap beforeCommit = Render(canvas);
        if (ReadPixel(beforeCommit, 20, 12).A == 0 || ReadPixel(beforeCommit, 20, 36).A == 0 ||
            canvas.Strokes.Count != 0 || canvas.Children.Count != 0 || changes != 0)
            throw new InvalidOperationException("A staged shape is not visible independently of the committed document.");

        var completed = new StrokeCollection(first);
        completed.Add(second);
        canvas.Strokes.Add(completed);
        canvas.SetShapePreview(null, null);
        if (changes != 1 || !ReferenceEquals(canvas.Strokes[0], first[0]) || !ReferenceEquals(canvas.Strokes[1], second[0]))
            throw new InvalidOperationException("Finishing a staged shape did not preserve ordered strokes in one collection change.");

        RenderTargetBitmap afterCommit = Render(canvas);
        if (ReadPixel(afterCommit, 20, 12) != ReadPixel(beforeCommit, 20, 12) ||
            ReadPixel(afterCommit, 20, 36) != ReadPixel(beforeCommit, 20, 36))
            throw new InvalidOperationException("A completed shape lost ink or retained stale preview pixels.");

        canvas.Strokes.Clear();
        if (ReadPixel(Render(canvas), 20, 12).A != 0)
            throw new InvalidOperationException("A completed shape left a stale preview attached to the native ink presenter.");

        CheckHighlighter(canvas, Colors.Black, false);
        CheckHighlighter(canvas, Colors.Blue, true);
    }

    private static void CheckHighlighter(ShapeDraftInkCanvas canvas, Color color, bool highlighter)
    {
        canvas.Strokes.Clear();
        canvas.Background = Brushes.White;
        var existing = CreateLine(32);
        existing.DrawingAttributes.Color = color;
        existing.DrawingAttributes.IsHighlighter = highlighter;
        canvas.Strokes.Add(existing);

        var draft = new Stroke(new StylusPointCollection(new[] { new Point(32, 8), new Point(32, 52) }),
            new DrawingAttributes { Width = 6, Height = 6, Color = Colors.Blue, IsHighlighter = true });
        canvas.SetShapePreview(null, new StrokeCollection { draft });
        var before = Render(canvas);
        canvas.SetShapePreview(null, null);
        canvas.Strokes.Add(draft);
        var after = Render(canvas);

        if (ReadPixel(before, 32, 32) != ReadPixel(after, 32, 32) ||
            ReadPixel(before, 32, 20) != ReadPixel(after, 32, 20) ||
            (!SystemParameters.HighContrast && ReadPixel(before, 32, 20) == Colors.White))
            throw new InvalidOperationException("Highlighter preview does not blend like committed ink.");
    }

    private static Stroke CreateLine(double y) => new(new StylusPointCollection(new[] { new Point(8, y), new Point(52, y) }),
        new DrawingAttributes { Width = 6, Height = 6, Color = Colors.Black });

    private static RenderTargetBitmap Render(UIElement element)
    {
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.Input);
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static Color ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
