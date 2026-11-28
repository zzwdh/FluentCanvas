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
        using var original = Freeze(4);
        storage.WriteSlide(first, 258, original);
        using var secondPage = Freeze(10);
        storage.WriteSlide(second, 258, secondPage);
        storage.SaveLastSlide(first, 258);
        Check(Value(storage, first, 258) == 4 && Value(storage, second, 258) == 10, "Slide IDs are scoped to a deck.");
        Check(storage.ReadLastSlide(first) == 258 && storage.ReadLastSlide(second) == null, "Resume uses a stable slide ID.");

        string blocked = Path.Combine(root, "unavailable-PowerPoint-location");
        File.WriteAllText(blocked, "This path is a file.");
        var unavailable = new PowerPointInkStorage(blocked, recovery);
        using var recoveryPage = Freeze(24);
        Check(unavailable.WriteSlide(first, 258, recoveryPage), "A failed preferred write uses user-local recovery.");
        unavailable.SaveLastSlide(first, 312);
        Check(Value(unavailable, first, 258) == 24 && unavailable.ReadLastSlide(first) == 312,
            "Recovered ink and resume position reopen even when the preferred path is unavailable.");
        using var primaryPage = Freeze(30);
        Check(!storage.WriteSlide(first, 258, primaryPage) && Value(storage, first, 258) == 30,
            "A successful preferred write supersedes the recovery copy.");
        byte[] damaged = { 1, 2, 3 };
        File.WriteAllBytes(storage.SlidePath(first, 258), damaged);
        try { storage.ReadSlide(first, 258); throw new Exception("Expected truncated ink."); }
        catch (EndOfStreamException) { storage.PreserveUnreadSlide(first, 258); }
        storage.WriteSlide(first, 258, primaryPage);
        storage.WriteSlide(first, 258, recoveryPage);
        string kept = System.Linq.Enumerable.Single(Directory.GetFiles(Path.GetDirectoryName(storage.SlidePath(first, 258)), "258.fcink.*.bak"));
        Check(damaged.AsSpan().SequenceEqual(File.ReadAllBytes(kept)) && Value(storage, first, 258) == 24,
            "An unreadable original survives repeated saves of new edits.");

        var loads = new PowerPointSlideLoad();
        var firstRead = loads.Begin();
        var firstOpen = loads.WaitAsync();
        Check(loads.Pending && !firstOpen.IsCompleted, "Activation waits for a delayed disk read.");
        var nextRead = loads.Begin();
        Check(!firstOpen.GetAwaiter().GetResult() && !loads.IsCurrent(firstRead) && loads.IsCurrent(nextRead),
            "Rapid slide navigation invalidates a stale read and its pending activation.");
        loads.End(true);
        Check(nextRead.GetAwaiter().GetResult() && !loads.Pending, "A completed read admits the waiting activation.");
        var duringClose = loads.Begin();
        loads.End(false);
        Check(!duringClose.GetAwaiter().GetResult(), "Closing a show cancels its waiting activation.");
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
        var initial = Freeze(61);
        byte[] original = initial.ExportInk();
        writer.SaveNativePage(storage, first, 258, initial);
        writer.SaveLastSlide(storage, first, 258);
        writer.WhenIdle().GetAwaiter().GetResult();
        Check(writer.HasPendingWrites && original.AsSpan().SequenceEqual(writer.ReadSlideAsync(storage, first, 258).GetAwaiter().GetResult())
            && writer.PendingLastSlide(first) == 258, "Failed writes retain both ink and resume position in memory.");
        Check(reports.TryPeek(out var error) && error.Error is IOException, "A background write failure is reported.");
        Check(!writer.FlushAsync().GetAwaiter().GetResult(), "An update is blocked while queued writes still fail.");

        File.Delete(selected);
        File.Delete(recovery);
        Check(writer.FlushAsync().GetAwaiter().GetResult() && Value(storage, first, 258) == 61,
            "Recovered storage can flush without closing the presentation writer.");
        writer.SaveNativePage(storage, first, 258, Freeze(72));
        writer.SaveNativePage(storage, first, 258, Freeze(83));
        writer.SaveNativePage(storage, second, 258, Freeze(94));
        writer.SaveLastSlide(storage, first, 312);
        writer.SaveNativePage(storage, second, 312, new TimeMachine().FreezeInk());
        writer.Close();
        Check(!writer.HasPendingWrites && writer.PendingLastSlide(first) == null,
            "Closing waits until the retained work is saved.");
        Check(Value(storage, first, 258) == 83 && Value(storage, second, 258) == 94,
            "Retried work never replaces the latest ink or crosses deck identities.");
        Check(storage.ReadSlide(second, 312) is { Length: 0 }, "An empty slide is a durable, distinct page.");
        Check(storage.ReadLastSlide(first) == 312 && storage.ReadLastSlide(second) == null,
            "The latest resume position survives closing.");
    }

    private static FrozenInk Freeze(int x)
    {
        var history = new TimeMachine();
        history.CommitStrokeUserInputHistory(new StrokeCollection {
            new Stroke(new StylusPointCollection { new StylusPoint(x, 1), new StylusPoint(x + 1, 2) })
        });
        return history.FreezeInk();
    }

    private static int Value(PowerPointInkStorage storage, string deck, int slide)
    {
        return (int)Math.Round(NativeStrokeCodec.DecodePage(storage.ReadSlide(deck, slide))[0].StylusPoints[0].X);
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
