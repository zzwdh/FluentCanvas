using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentCanvas.Helpers;

internal static class SelectionExportTests
{
    internal static void Verify()
    {
        var canvas = new InkCanvas();
        var red = AddImage(canvas, 100, 75, 40, 20, Colors.Red);
        red.RenderTransform = new RotateTransform(90);
        red.RenderTransformOrigin = new Point(0.5, 0.5);
        var blue = AddImage(canvas, 130, 70, 20, 20, Colors.Blue);
        canvas.Measure(new Size(400, 300));
        canvas.Arrange(new Rect(0, 0, 400, 300));

        var points = new StylusPointCollection { new StylusPoint(112, 90), new StylusPoint(126, 90) };
        canvas.Strokes.Add(new Stroke(points, new DrawingAttributes { Color = Colors.Lime, Width = 4, Height = 4 }));
        canvas.Select(canvas.Strokes, new UIElement[] { blue, red });

        var bitmap = CanvasSelectionImage.Render(canvas);
        using var png = new MemoryStream();
        int uiThread = Environment.CurrentManagedThreadId;
        var writes = new ScreenshotWriteQueue();
        var failure = writes.Enqueue(() => { throw new IOException("Simulated screenshot failure."); });
        var encoding = writes.Enqueue(() =>
        {
            if (Environment.CurrentManagedThreadId == uiThread) throw new InvalidOperationException("Encoded on the UI thread.");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(png);
        });
        writes.Drain();
        encoding.GetAwaiter().GetResult();
        if (failure.Exception?.InnerException is not IOException)
            throw new InvalidOperationException("Unable to observe the failed write independently of the next screenshot.");
        png.Position = 0;
        var frame = BitmapFrame.Create(png);
        if (frame.PixelWidth != 50 || frame.PixelHeight != 50)
            throw new InvalidOperationException($"Unexpected selection dimensions {frame.PixelWidth} × {frame.PixelHeight}.");

        var decoded = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        CheckPixel(decoded, 12, 27, Colors.Red);
        CheckPixel(decoded, 31, 13, Colors.Blue);
        CheckPixel(decoded, 14, 30, Colors.Lime);
        CheckPixel(decoded, 2, 2, Color.FromArgb(0, 0, 0, 0));
    }

    private static Image AddImage(InkCanvas canvas, double left, double top, double width, double height, Color color)
    {
        var image = new Image
        {
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new[] { color.B, color.G, color.R, color.A }, 4)
        };
        InkCanvas.SetLeft(image, left);
        InkCanvas.SetTop(image, top);
        canvas.Children.Add(image);
        return image;
    }

    private static void CheckPixel(BitmapSource bitmap, int x, int y, Color expected)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        if (!pixel.SequenceEqual(new[] { expected.B, expected.G, expected.R, expected.A }))
            throw new InvalidOperationException($"Unexpected color at ({x}, {y}): {string.Join(", ", pixel)}.");
    }
}
