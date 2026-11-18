using FluentCanvas.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class Program
{
    private static readonly Stroke Sample = new(new StylusPointCollection { new StylusPoint(2, 3), new StylusPoint(20, 30) });
    private static string root;
    private static int passed;

    [STAThread]
    private static int Main()
    {
        root = Directory.CreateTempSubdirectory("FluentCanvas-DocumentTests-").FullName;
        try
        {
            Run("Portable artwork with two same-named images, transforms, Chinese and long paths", RoundTrip);
            Run("WPF XamlWriter legacy canvas and absolute source resolved from package", ReadLegacy);
            Run("Packaged media opens after deleting its original and can be re-saved", MediaRoundTrip);
            Run("ZIP entry checksum mismatch preserves current document", BadChecksum);
            Run("Raw ISF and old PowerPoint raw .icart, read-only association", RawInk);
            Run("File association repeated opens preserve per-page undo identities", Reopen);
            Run("Corrupt archive preserves canvas objects and undo/redo cursor", () => FailedOpen("bad.icart", p => File.WriteAllText(p, "broken")));
            Run("Invalid standalone strokes preserve document", () => FailedOpen("bad.icstk", p => File.WriteAllText(p, "broken")));
            Run("Missing resource after a valid image preserves previous document", () => FailedOpen("missing.icart", p =>
                Build(p, "<Image Source='good.png'/><Image Source='missing.png'/>", new() { ["File Dependency/good.png"] = Pixel(0xff) })));
            Run("Unexpected XML node rejected before commit", () => FailedOpen("node.icart", p => Build(p, "<Button/>")));
            Run("Unsupported XAML attributes, namespaces and markup rejected", InvalidXml);
            Run("Duplicate resource basenames in ambiguous old package rejected", () => FailedOpen("duplicate.icart", p =>
                Build(p, "", new() { ["File Dependency/a.png"] = Pixel(0), ["File Dependency/A.PNG"] = Pixel(1) })));
            Run("Unexpected archive directories rejected without extraction", () => FailedOpen("paths.icart", p =>
                Build(p, "", new() { ["unrelated/file.dat"] = [0] })));
            Run("Archive entry limit enforced before allocation", () => FailedOpen("count.icart", p =>
                Build(p, "", Enumerable.Range(0, 512).ToDictionary(i => "File Dependency/" + i, i => Array.Empty<byte>()))));
            Run("Invalid image content rejected", () => FailedOpen("invalid-image.icart", p =>
                Build(p, "<Image Source='bad.png'/>", new() { ["File Dependency/bad.png"] = [1,2,3] })));
            Run("Mid-save error and sharing violation leave original intact", FailedSave);
            Run("Failed commit restores attached elements, strokes and prior history", CommitRollback);
            Console.WriteLine($"{passed} document regression groups passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool result) { if (!result) throw new Exception("Assertion failed."); }
    private static string PathFor(string name) => Path.Combine(root, name);
    private static byte[] Pixel(byte blue)
    {
        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] {blue, 0, 42, 255}, 4);
        using var source = new MemoryStream();
        var writer = new PngBitmapEncoder();
        writer.Frames.Add(BitmapFrame.Create(pixel));
        writer.Save(source);
        return source.ToArray();
    }

    private static Image Insert(string dir, byte blue)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "同名 图片.png");
        File.WriteAllBytes(path, Pixel(blue));
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        return new Image { Source = bitmap, Width = 56, Height = 42 };
    }

    private static byte Blue(Image image)
    {
        var data = new byte[4];
        new FormatConvertedBitmap((BitmapSource)image.Source, PixelFormats.Bgra32, null, 0).CopyPixels(data, 4, 0);
        return data[0];
    }

    private static void RoundTrip()
    {
        var first = Insert(PathFor("甲"), 0x27);
        var second = Insert(PathFor("乙"), 0xc4);
        var canvas = new InkCanvas();
        canvas.Strokes.Add(Sample.Clone());
        canvas.Children.Add(first);
        canvas.Children.Add(second);
        InkCanvas.SetLeft(first, 25.25);
        first.RenderTransform = new ScaleTransform(2.5, 3);
        string path = PathFor("原作.icart");
        CanvasDocument.Save(path, canvas);
        var folder = root;
        for (var i = 0; i < 7; i++) folder = Path.Combine(folder, new string('a', 32));
        Directory.CreateDirectory(folder);
        string moved = Path.Combine(folder, "新 文档.icart");
        File.Move(path, moved);
        Directory.Delete(PathFor("甲"), true);
        Directory.Delete(PathFor("乙"), true);
        CanvasDocument.LoadInto(moved, canvas, new TimeMachine());
        Check(canvas.Strokes.Count == 1 && canvas.Children.Count == 2);
        Check(Blue((Image)canvas.Children[0]) == 0x27 && Blue((Image)canvas.Children[1]) == 0xc4);
        Check(InkCanvas.GetLeft(canvas.Children[0]) == 25.25 && canvas.Children[0].RenderTransform.Value.M11 == 2.5);
        CanvasDocument.Save(path, canvas);
    }

    private static void ReadLegacy()
    {
        var canvas = new InkCanvas { Name = "inkCanvas", Cursor = Cursors.Pen, IsManipulationEnabled = true, DefaultDrawingAttributes = new DrawingAttributes() };
        canvas.Strokes.Add(Sample.Clone());
        var image = Insert(PathFor("old"), 0x8c);
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(2, 3));
        group.Children.Add(new TranslateTransform(15, 77));
        image.RenderTransform = group;
        canvas.Children.Add(image);
        string xml = XamlWriter.Save(canvas);
        Build(PathFor("legacy.icart"), xml, new() { ["File Dependency/同名 图片.png"] = Pixel(0x8c) }, fullXml:true);
        Directory.Delete(PathFor("old"), true);
        CanvasDocument.LoadInto(PathFor("legacy.icart"), canvas, new TimeMachine());
        Check(canvas.Children.Count == 1 && Blue((Image)canvas.Children[0]) == 0x8c);
        Check(canvas.Children[0].RenderTransform.Value == group.Value);
    }

    private static void RawInk()
    {
        using var data = new MemoryStream();
        new StrokeCollection { Sample.Clone() }.Save(data);
        foreach (string name in new[] { "raw.icstk", "raw-slide.icart" })
        {
            string path = PathFor(name);
            File.WriteAllBytes(path, data.ToArray());
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                var canvas = new InkCanvas();
                CanvasDocument.LoadInto(path, canvas, new TimeMachine());
                Check(canvas.Strokes.Count == 1);
                using var slide = CanvasDocument.ReadSlideInk(path);
                Check(slide.ToArray().SequenceEqual(data.ToArray()));
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
        }
    }

    private static void MediaRoundTrip()
    {
        var original = PathFor("视频.mp4");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "frame.mp4"), original);
        var canvas = new InkCanvas();
        canvas.Children.Add(new MediaElement { Source = new Uri(original), Width = 90, Height = 90 });
        string artwork = PathFor("clip.icart");
        CanvasDocument.Save(artwork, canvas);
        File.Delete(original);
        CanvasDocument.LoadInto(artwork, canvas, new TimeMachine());
        var media = (MediaElement)canvas.Children[0];
        Check(File.Exists(media.Source.LocalPath) && media.Source.LocalPath != original);
        CanvasDocument.Save(artwork, canvas);
    }

    private static void BadChecksum()
    {
        FailedOpen("checksum.icart", path =>
        {
            Build(path, "");
            var bytes = File.ReadAllBytes(path);
            int directory = bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 1, 2 });
            Check(directory >= 0);
            bytes[directory + 16] ^= 1;
            File.WriteAllBytes(path, bytes);
        });
    }

    private static void Reopen()
    {
        Build(PathFor("empty.icart"), "");
        var canvas = new InkCanvas();
        var history = new TimeMachine();
        CanvasDocument.LoadInto(PathFor("原作.icart"), canvas, history);
        Check(ReferenceEquals(history.Undo().Element, canvas.Children[1]));
        CanvasDocument.LoadInto(PathFor("empty.icart"), canvas, history);
        Check(canvas.Children.Count == 0 && history.Undo().CurrentStroke[0] == canvas.Strokes[0]);
    }

    private static void Build(string path, string xml, Dictionary<string, byte[]> assets = null, bool fullXml = false)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using (var s = zip.CreateEntry("strokes.icstk").Open()) new StrokeCollection {Sample.Clone()}.Save(s);
        using (var s = new StreamWriter(zip.CreateEntry("elements.xaml").Open()))
            s.Write(fullXml ? xml : "<InkCanvas xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" + xml + "</InkCanvas>");
        if (assets != null) foreach (var asset in assets)
        {
            using var s = zip.CreateEntry(asset.Key).Open();
            s.Write(asset.Value);
        }
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch { return; }
        throw new Exception("Expected the operation to fail.");
    }

    private static void FailedOpen(string filename, Action<string> build)
    {
        string path = PathFor(filename);
        build(path);
        var canvas = new InkCanvas();
        var history = new TimeMachine();
        var child = new Image();
        canvas.Children.Add(child);
        canvas.Strokes.Add(Sample);
        history.CommitStrokeUserInputHistory(new StrokeCollection { Sample });
        history.CommitElementInsertHistory(child);
        history.Undo();
        ExpectFailure(() => CanvasDocument.LoadInto(path, canvas, history));
        Check(canvas.Strokes.Single() == Sample && ReferenceEquals(canvas.Children[0], child));
        Check(history.Redo().Element == child);
    }

    private static void InvalidXml()
    {
        foreach (string xml in new[] { "<Image ToolTip='b'/>", "<Image Source='{Binding}'/>", "<Image xmlns='urn:unknown'/>" })
            FailedOpen("xml.icart", p => Build(p, xml));
    }

    private static void FailedSave()
    {
        string path = PathFor("keep.icart");
        Build(path, "");
        byte[] before = File.ReadAllBytes(path);
        var canvas = new InkCanvas();
        canvas.Children.Add(new Button());
        ExpectFailure(() => CanvasDocument.Save(path, canvas));
        Check(File.ReadAllBytes(path).SequenceEqual(before));
        ExpectFailure(() => AtomicFile.Write(path, output =>
        {
            output.WriteByte(5);
            throw new OperationCanceledException();
        }));
        Check(File.ReadAllBytes(path).SequenceEqual(before));
        canvas.Children.Clear();
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            ExpectFailure(() => CanvasDocument.Save(path, canvas));
        Check(File.ReadAllBytes(path).SequenceEqual(before));
        Check(!Directory.EnumerateFiles(root, "*.tmp").Any());
    }

    private static void CommitRollback()
    {
        string path = PathFor("commit.icart");
        Build(path, "");
        var canvas = new InkCanvas();
        var child = new Image();
        canvas.Children.Add(child);
        canvas.Strokes.Add(Sample);
        var history = new TimeMachine();
        history.CommitElementInsertHistory(child);
        void Reject(bool _) => throw new InvalidOperationException("Simulated commit failure");
        history.OnUndoStateChanged += Reject;
        ExpectFailure(() => CanvasDocument.LoadInto(path, canvas, history));
        history.OnUndoStateChanged -= Reject;
        Check(canvas.Children[0] == child && canvas.Strokes.Single() == Sample && history.Undo().Element == child);
    }
}


