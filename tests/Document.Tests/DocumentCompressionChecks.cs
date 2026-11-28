using FluentCanvas.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class DocumentCompressionChecks
{
    internal static void Verify(string root)
    {
        var canvas = new InkCanvas();
        // One record larger than a block followed by several whole-record blocks.
        canvas.Strokes.Add(PressureStroke(0, 15001));
        for (int i = 1; i <= 80; i++) canvas.Strokes.Add(PressureStroke(i, 201));
        var history = new TimeMachine();
        history.CommitStrokeUserInputHistory(canvas.Strokes);
        using var page = history.FreezeInk();
        var expected = page.ExportInk();
        using var stream = new MemoryStream();
        NativeInkDocument.Write(stream, page);
        if (BitConverter.ToInt32(stream.GetBuffer(), 8) != 2 || stream.Length >= expected.Length)
            throw new Exception("The page was not stored in compressed v2 blocks.");
        stream.Position = 0;
        if (!NativeInkDocument.Read(stream).SequenceEqual(expected)) throw new Exception("Ink payload changed.");

        string doc = Path.Combine(root, "shared-images.icart");
        for (int i = 0; i < 2; i++)
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[]{ 40, 200, 30, 255 }, 4);
            image.Freeze();
            canvas.Children.Add(new Image { Source = image, Width = 30, Height = 15 });
        }
        using (var snapshot = CanvasDocument.Capture(history.FreezeInk(), canvas.Children.Cast<UIElement>()))
            snapshot.Save(doc);
        using (var file = ZipFile.OpenRead(doc))
            if (file.Entries.Count(e => e.FullName.StartsWith("File Dependency/")) != 1)
                throw new Exception("Identical encoded pictures should share an archive entry.");
        CanvasDocument.LoadInto(doc, canvas, new TimeMachine());
        if (canvas.Children.Count != 2 || ((Image)canvas.Children[0]).Source != ((Image)canvas.Children[1]).Source)
            throw new Exception("Shared images did not restore as independent scene objects.");
    }

    private static Stroke PressureStroke(int line, int length)
    {
        var desc = new StylusPointDescription([
            new(StylusPointProperties.X), new(StylusPointProperties.Y), new(StylusPointProperties.NormalPressure),
            new(StylusPointProperties.XTiltOrientation), new(StylusPointProperties.YTiltOrientation)]);
        var points = new StylusPointCollection(desc, length);
        for (int i = 0; i < length; i++)
            points.Add(new StylusPoint((line % 12) * 20 + i * .125, line + 6 * Math.Sin(i * .11),
                (float)(.5 + .42 * Math.Cos(i * .03)), desc, [i % 45, (line + i) % 31]));
        return new Stroke(points);
    }

    private static IEnumerable<InkCanvas> Inputs(string kind, string[] paths)
    {
        if (kind == "mathwriting")
        {
            foreach (string file in Directory.EnumerateFiles(paths.Single(), "*.inkml").Order(StringComparer.Ordinal))
            {
                var xml = XDocument.Load(file);
                var page = new InkCanvas();
                foreach (var trace in xml.Descendants(XName.Get("trace", "http://www.w3.org/2003/InkML")))
                {
                    var points = new StylusPointCollection();
                    foreach (string sample in trace.Value.Split(','))
                    {
                        var xy = sample.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        points.Add(new StylusPoint(double.Parse(xy[0], CultureInfo.InvariantCulture),
                            double.Parse(xy[1], CultureInfo.InvariantCulture)));
                    }
                    if (points.Count != 0) page.Strokes.Add(new Stroke(points));
                }
                yield return page;
            }
        }
        else if (kind == "course")
        {
            foreach (string file in paths)
            {
                var page = new InkCanvas();
                CanvasDocument.LoadInto(file, page, new TimeMachine());
                yield return page;
            }
        }
        else if (kind is "geometry" or "pressure")
        {
            var page = new InkCanvas();
            var brush = new DrawingAttributes();
            for (int i = 0; i < 450; i++)
            {
                var a = new Point(i % 25 * 20, i / 25 * 20);
                page.Strokes.Add(kind == "pressure" ? PressureStroke(i, 96) :
                    ParametricInk.Ellipse(a, a + new Vector(120 + i % 43, 80 + i % 31)).Create(brush));
            }
            yield return page;
        }
        else throw new ArgumentException("Unknown benchmark set.");
    }

    internal static void Measure(string root, string kind, string[] paths)
    {
        long v1 = 0, v2 = 0, works = 0;
        int pages = 0, strokes = 0;
        var saveTimes = new List<double>();
        var loadTimes = new List<double>();
        foreach (var page in Inputs(kind, paths))
        {
            var history = new TimeMachine();
            if (page.Strokes.Count != 0) history.CommitStrokeUserInputHistory(page.Strokes);
            using var frozen = history.FreezeInk();
            var raw = frozen.ExportInk();
            using var old = new MemoryStream();
            using (var zipEntry = new DeflateStream(old, CompressionLevel.Fastest, true))
            using (var writer = new BinaryWriter(zipEntry))
            {
                writer.Write("FCINK\r\n\u001a"u8);
                writer.Write(1); writer.Write(raw.Length); writer.Write(raw); writer.Write(SHA256.HashData(raw));
            }
            string file = Path.Combine(root, "measured.icart");
            var sw = Stopwatch.StartNew();
            using (var snapshot = CanvasDocument.Capture(history.FreezeInk(), page.Children.Cast<UIElement>()))
                snapshot.Save(file);
            saveTimes.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            var view = new InkCanvas();
            CanvasDocument.LoadInto(file, view, new TimeMachine());
            view.Measure(new Size(1280, 720));
            view.Arrange(new Rect(0, 0, 1280, 720));
            new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32).Render(view);
            loadTimes.Add(sw.Elapsed.TotalMilliseconds);
            if (view.Strokes.Count != page.Strokes.Count || view.Children.Count != page.Children.Count) throw new Exception("Round trip diverged.");
            using var zip = ZipFile.OpenRead(file);
            var entry = zip.GetEntry(NativeInkDocument.EntryName);
            using var stream = entry.Open();
            if (!NativeInkDocument.Read(stream, entry.Length, entry.Crc32).SequenceEqual(raw)) throw new Exception("Changed encoded samples.");
            pages++; strokes += view.Strokes.Count;
            v2 += entry.CompressedLength; v1 += old.Length; works += new FileInfo(file).Length;
        }
        if (pages == 0) throw new Exception("No benchmark pages.");
        Console.WriteLine($"{kind}: {pages} pages, {strokes} strokes, v1/v2 ink bytes {v1}/{v2}, full works {works} B, " +
            $"mean full save {saveTimes.Average():F2} ms/page, mean open+first offscreen frame {loadTimes.Average():F2} ms/page, " +
            $"process peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F1} MiB.");
    }
}
