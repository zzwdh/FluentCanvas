using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Ink;

namespace FluentCanvas.Helpers;

// The render objects stay on the dispatcher. Rust owns the ordered records, cursor and revisions.
internal sealed partial class NativeInkPage : SafeHandleZeroOrMinusOneIsInvalid
{
    private const string Library = "fluent_canvas_core";
    private const CallingConvention Abi = CallingConvention.Cdecl;
    private sealed class Identity
    {
        internal ulong Value { get; }
        internal ulong RenderedVersion { get; set; }
        internal Identity(Stroke stroke, ulong id)
        {
            Value = id;
            stroke.Invalidated += (_, _) => RenderedVersion = 0;
            stroke.PropertyDataChanged += (_, _) => RenderedVersion = 0;
        }
    }
    private static readonly ConditionalWeakTable<Stroke, Identity> identities = new();
    private static long nextId;

    static NativeInkPage()
    {
        if (fc_core_abi_version() != 3)
            throw new InvalidOperationException("Unsupported Fluent Canvas ink core ABI.");
    }

    private NativeInkPage() : base(true) { }

    internal static NativeInkPage Create()
    {
        var page = Own(fc_core_new());
        page.SetPageId(Guid.NewGuid());
        return page;
    }

    private static NativeInkPage Own(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Could not create a native ink page.");
        var page = new NativeInkPage();
        page.SetHandle(handle);
        return page;
    }

    internal NativeInkPage Copy() => Own(fc_core_clone(this));

    protected override bool ReleaseHandle()
    {
        fc_core_free(handle);
        return true;
    }

    internal static ulong Id(Stroke stroke) =>
        identities.GetValue(stroke, key => new Identity(key, (ulong)Interlocked.Increment(ref nextId))).Value;

    internal readonly record struct HistoryState(int Cursor, int Count, ulong Revision, ulong SavedRevision)
    {
        internal bool Dirty => Revision != SavedRevision;
    }

    internal HistoryState State
    {
        get
        {
            Check(fc_core_state(this, out var cursor, out var total, out var current, out var saved));
            return new(checked((int)cursor), checked((int)total), current, saved);
        }
    }

    internal void MarkSaved() => Check(fc_core_mark_saved(this));
    internal void Undo() => Check(fc_core_undo(this));
    internal void Redo() => Check(fc_core_redo(this));

    internal void Append(IEnumerable<Stroke> strokes)
    {
        var bytes = Records(strokes);
        Check(fc_core_append(this, bytes.Array, (nuint)bytes.Count));
        RememberVisuals(strokes);
    }

    internal bool FinishStroke(Stroke stroke, int style, double elapsedMilliseconds)
    {
        ulong id = Id(stroke);
        int status = fc_core_finish_stroke(this, id, style, elapsedMilliseconds, out var changed);
        if (status == 2) return false; // The delayed completion may refer to a removed stroke.
        Check(status);
        if (changed != 0)
        {
            stroke.StylusPoints = Read(id).StylusPoints;
            Remember(stroke);
        }
        return changed != 0;
    }

    internal void Apply(ulong[] order, IEnumerable<Stroke> changed, bool reset = false)
    {
        var bytes = Records(changed);
        Check(fc_core_apply(this, order, (nuint)order.Length, bytes.Array, (nuint)bytes.Count, reset ? 1 : 0));
        RememberVisuals(changed);
    }

    private static ArraySegment<byte> Records(IEnumerable<Stroke> strokes)
    {
        if (strokes == null) return default;
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        foreach (var stroke in strokes) NativeStrokeCodec.Write(writer, Id(stroke), stroke);
        return new ArraySegment<byte>(data.GetBuffer(), 0, checked((int)data.Length));
    }

    internal ulong[] Order()
    {
        CheckSize(fc_core_order(this, null, 0, out var count));
        if (count == 0) return [];
        var ids = new ulong[checked((int)count)];
        Check(fc_core_order(this, ids, count, out _));
        return ids;
    }

    internal Stroke Read(ulong id)
    {
        CheckSize(fc_core_read(this, id, null, 0, out var count));
        var buffer = new byte[checked((int)count)];
        Check(fc_core_read(this, id, buffer, count, out _));
        var stroke = NativeStrokeCodec.Decode(buffer);
        identities.Add(stroke, new Identity(stroke, id) { RenderedVersion = RecordVersion(id) });
        return stroke;
    }

    internal byte[] ExportInk()
    {
        CheckSize(fc_core_export(this, null, 0, out var count));
        if (count == 0) return [];
        var buffer = new byte[checked((int)count)];
        Check(fc_core_export(this, buffer, count, out _));
        return buffer;
    }

    internal StrokeCollection MaterializeInk()
    {
        var strokes = NativeStrokeCodec.DecodePage(ExportInk(),
            (stroke, id) => identities.Add(stroke, new Identity(stroke, id)));
        RememberMaterializedPage(strokes);
        return strokes;
    }

    private void RememberMaterializedPage(StrokeCollection strokes)
    {
        if (strokes.Count > 0)
        {
            var versions = new ulong[strokes.Count];
            Check(fc_core_versions(this, versions, (nuint)versions.Length));
            for (int i = 0; i < strokes.Count; i++)
                if (identities.TryGetValue(strokes[i], out var identity)) identity.RenderedVersion = versions[i];
        }

    }

    private static void CheckSize(int result)
    {
        if (result != 3) Check(result);
    }

    private static void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException($"Native ink operation failed ({result}).");
    }

    [DllImport(Library, CallingConvention = Abi)]
    private static extern uint fc_core_abi_version();
    [DllImport(Library, CallingConvention = Abi)]
    private static extern IntPtr fc_core_new();
    [DllImport(Library, CallingConvention = Abi)]
    private static extern void fc_core_free(IntPtr page);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern IntPtr fc_core_clone(NativeInkPage page);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_state(NativeInkPage page, out nuint cursor, out nuint total, out ulong token, out ulong saved);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_mark_saved(NativeInkPage page);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_undo(NativeInkPage page);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_redo(NativeInkPage page);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_append(NativeInkPage page, byte[] data, nuint length);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_finish_stroke(NativeInkPage page, ulong id, int style, double elapsedMilliseconds, out int changed);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_apply(NativeInkPage page, ulong[] order, nuint count, byte[] data, nuint length, int reset);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_order(NativeInkPage page, [Out] ulong[] order, nuint capacity, out nuint required);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_export(NativeInkPage page, [Out] byte[] data, nuint capacity, out nuint required);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_read(NativeInkPage page, ulong id, [Out] byte[] data, nuint capacity, out nuint required);
}
