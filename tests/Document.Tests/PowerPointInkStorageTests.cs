using FluentCanvas.Helpers;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Ink;
using System.Windows.Input;

internal static class PowerPointInkStorageTests
{
    internal static void Run(string root)
    {
        var name = "同名课件.pptx";
        var first = PowerPointInkStorage.GetDeckKey(Path.Combine(root, "第一班", name));
        var second = PowerPointInkStorage.GetDeckKey(Path.Combine(root, "第二班", name));
        Check(first != second, "Same-named presentations in different folders have distinct storage.");
        Check(first == PowerPointInkStorage.GetDeckKey(Path.Combine(root, "第一班", name.ToUpperInvariant())),
            "Windows file path casing does not change identity.");

        var selected = Path.Combine(root, "presentations");
        var recovery = Path.Combine(root, "recovery");
        var storage = new PowerPointInkStorage(selected, recovery);
        byte[] original = Ink(4);
        storage.WriteSlide(first, 258, original);
        storage.WriteSlide(second, 258, Ink(10));
        storage.SaveLastSlide(first, 258);
        Check(Value(storage, first, 258) == 4 && Value(storage, second, 258) == 10, "Slide IDs are scoped to a deck.");
        Check(storage.ReadLastSlide(first) == 258 && storage.ReadLastSlide(second) == null, "Resume uses a stable slide ID.");

        var legacy = Path.Combine(selected, "Auto Saved - Presentations", name + "_2");
        Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "0001.icstk"), Ink(7));
        File.WriteAllBytes(Path.Combine(legacy, "0002.icart"), Ink(8));
        Check(storage.GetLegacyFolder(first, name, 2) == legacy, "Legacy storage is discoverable before explicit import.");
        Check(storage.ImportLegacy(first, legacy, [312, 258]) == 1, "Legacy import skips an existing destination slide.");
        Check(Value(storage, first, 312) == 7 && Value(storage, first, 258) == 4, "Legacy import maps by supplied stable IDs.");
        Check(storage.GetLegacyFolder(first, name, 2) == null && File.Exists(Path.Combine(legacy, "0001.icstk")),
            "Migration records completion and leaves the source.");

        string blocked = Path.Combine(root, "unavailable-PowerPoint-location");
        File.WriteAllText(blocked, "This path is a file.");
        var unavailable = new PowerPointInkStorage(blocked, recovery);
        Check(unavailable.WriteSlide(first, 258, Ink(24)), "A failed preferred write uses user-local recovery.");
        unavailable.SaveLastSlide(first, 312);
        Check(Value(unavailable, first, 258) == 24 && unavailable.ReadLastSlide(first) == 312,
            "Recovered ink and resume position reopen even when the preferred path is unavailable.");
        Check(!storage.WriteSlide(first, 258, Ink(30)) && Value(storage, first, 258) == 30,
            "A successful preferred write supersedes the recovery copy.");
    }

    internal static void VerifyQueuedWrites(string root)
    {
        string selected = Path.Combine(root, "queued-slide-root");
        string recovery = Path.Combine(root, "queued-slide-recovery");
        File.WriteAllText(selected, "Temporarily unavailable as a directory.");
        File.WriteAllText(recovery, "Temporarily unavailable as a directory.");
        var storage = new PowerPointInkStorage(selected, recovery);
        var first = PowerPointInkStorage.GetDeckKey(Path.Combine(root, "first.pptx"));
        var second = PowerPointInkStorage.GetDeckKey(Path.Combine(root, "second.pptx"));
        var reports = new ConcurrentQueue<PowerPointInkWriter.Result>();
        var writer = new PowerPointInkWriter(reports.Enqueue);
        byte[] initial = Ink(61);
        writer.SaveSlide(storage, first, 258, initial);
        writer.SaveLastSlide(storage, first, 258);
        writer.WhenIdle().GetAwaiter().GetResult();
        Check(writer.HasPendingWrites && ReferenceEquals(writer.PendingInk(first, 258), initial)
            && writer.PendingLastSlide(first) == 258, "Failed writes retain both ink and resume position in memory.");
        Check(reports.TryPeek(out var error) && error.Error is IOException, "A background write failure is reported.");
        Check(!writer.FlushAsync().GetAwaiter().GetResult(), "An update is blocked while queued writes still fail.");

        File.Delete(selected);
        File.Delete(recovery);
        Check(writer.FlushAsync().GetAwaiter().GetResult() && Value(storage, first, 258) == 61,
            "Recovered storage can flush without closing the presentation writer.");
        writer.SaveSlide(storage, first, 258, Ink(72));
        writer.SaveSlide(storage, first, 258, Ink(83));
        writer.SaveSlide(storage, second, 258, Ink(94));
        writer.SaveLastSlide(storage, first, 312);
        writer.Close();
        Check(!writer.HasPendingWrites && writer.PendingInk(first, 258) == null && writer.PendingLastSlide(first) == null,
            "Closing waits until the retained work is saved.");
        Check(Value(storage, first, 258) == 83 && Value(storage, second, 258) == 94,
            "Retried work never replaces the latest ink or crosses deck identities.");
        Check(storage.ReadLastSlide(first) == 312 && storage.ReadLastSlide(second) == null,
            "The latest resume position survives closing.");
    }

    private static byte[] Ink(int x)
    {
        using var stream = new MemoryStream();
        var strokes = new StrokeCollection
        {
            new Stroke(new StylusPointCollection { new StylusPoint(x, 1), new StylusPoint(x + 1, 2) })
        };
        strokes.Save(stream);
        return stream.ToArray();
    }

    private static int Value(PowerPointInkStorage storage, string deck, int slide)
    {
        return (int)Math.Round(CanvasDocument.LoadSlideInk(storage.LatestSlidePath(deck, slide))[0].StylusPoints[0].X);
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
