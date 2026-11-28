using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Ink;

namespace FluentCanvas.Helpers;

internal sealed partial class NativeInkPage
{
    internal static NativeInkPage LoadDocument(byte[] records, out StrokeCollection strokes, bool undoable = false)
    {
        var page = LoadDocument(records, undoable, out ulong first);
        try
        {
            strokes = NativeStrokeCodec.DecodePage(records, (stroke, _) =>
                identities.Add(stroke, new Identity(stroke, first++)));
            page.RememberMaterializedPage(strokes);
            return page;
        }
        catch { page.Dispose(); throw; }
    }

    private static NativeInkPage LoadDocument(byte[] records, bool undoable, out ulong first)
    {
        int count = 0;
        for (int offset = 0; offset < records.Length; count++)
            offset += NativeStrokeCodec.RecordSize(records.AsSpan(offset));
        first = checked((ulong)(Interlocked.Add(ref nextId, count) - count + 1));
        var page = Create();
        try
        {
            int status = fc_core_load_document(page, records, (nuint)records.Length, first, undoable ? 1 : 0);
            if (status != 0) throw new InvalidDataException($"Invalid native ink document ({status}).");
            return page;
        }
        catch { page.Dispose(); throw; }
    }

    internal int Position(ulong id)
    {
        int code = fc_core_position(this, id, out var position);
        if (code == 2) return -1;
        Check(code);
        return checked((int)position);
    }

    internal void UpdateRecords(IEnumerable<Stroke> changed, bool onlyBrush)
    {
        using var records = new MemoryStream();
        using var edits = new MemoryStream();
        using var fullWriter = new BinaryWriter(records);
        using var brushWriter = new BinaryWriter(edits);
        if (changed != null)
            foreach (var stroke in changed.Distinct())
            {
                ulong id = Id(stroke);
                if (Position(id) < 0) continue;
                if (onlyBrush && NativeStrokeCodec.WriteBrushEdit(brushWriter, id, stroke)) continue;
                NativeStrokeCodec.Write(fullWriter, id, stroke);
            }
        Check(fc_core_update(this, records.GetBuffer(), (nuint)records.Length, edits.GetBuffer(), (nuint)edits.Length));
        if (changed != null)
            foreach (var stroke in changed)
                if (identities.TryGetValue(stroke, out var entry) &&
                    fc_core_record_version(this, entry.Value, out var version) == 0) entry.RenderedVersion = version;
    }

    internal void ChangeLayers(StrokeCollection visible, StrokeCollection removed, StrokeCollection inserted)
    {
        // A split fragment may be created and removed again in one eraser gesture.
        var removedIds = removed?.Select(Id).ToHashSet() ?? new();
        if (visible != null && inserted != null && inserted.Any(stroke => removedIds.Contains(Id(stroke)) && Position(Id(stroke)) >= 0))
        {
            var sequence = visible.Select(Id).ToArray();
            var live = sequence.ToHashSet();
            Apply(sequence, inserted.Where(stroke => live.Contains(Id(stroke))).Distinct());
            return;
        }
        var old = removedIds.Select(id => (Id: id, Position: Position(id))).Where(x => x.Position >= 0).ToArray();
        var additions = inserted?.Where(stroke => !removedIds.Contains(Id(stroke))).Distinct().ToArray() ?? [];
        int first = old.Length == 0 ? 0 : old.Min(x => x.Position);
        if (removedIds.Count == 0 && additions.Length > 0)
        {
            if (visible == null) first = Order().Length;
            else
            {
                // Pure insertion of a consecutive group: unchanged prefix has identical ranks.
                int lo = 0, hi = visible.Count - additions.Length;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (Position(Id(visible[mid])) == mid) lo = mid + 1;
                    else hi = mid;
                }
                first = lo;
            }
        }
        var positions = new ulong[additions.Length];
        bool consecutive = visible == null || first + additions.Length <= visible.Count;
        for (int i = 0; i < additions.Length && consecutive; i++)
        {
            consecutive = visible == null || ReferenceEquals(visible[first + i], additions[i]);
            positions[i] = (ulong)(first + i);
        }
        if (!consecutive)
        {
            // The surviving layers stay in relative order. Their native ranks tell
            // us how many final fragments precede a position, even if an eraser
            // gesture touched several disjoint ranges in reverse temporal order.
            var erasedRanks = old.Select(x => x.Position).Order().ToArray();
            var pending = additions.ToDictionary(Id);
            int low = 0;
            for (int i = 0; i < additions.Length; i++)
            {
                int high = visible.Count - 1;
                while (low < high)
                {
                    int mid = low + (high - low) / 2;
                    int original = Position(Id(visible[mid]));
                    if (original < 0) { high = mid; continue; }
                    int before = Array.BinarySearch(erasedRanks, original);
                    if (before >= 0) throw new InvalidOperationException("An erased layer is still attached.");
                    int erasedBefore = ~before;
                    int precedingAdditions = mid - (original - erasedBefore);
                    if (precedingAdditions > i) high = mid;
                    else low = mid + 1;
                }
                var stroke = visible[low];
                if (!pending.Remove(Id(stroke))) throw new InvalidOperationException("Unexpected replacement ink layer.");
                additions[i] = stroke;
                positions[i] = (ulong)low++;
            }
        }
        var data = Records(additions);
        var ids = old.Select(x => x.Id).ToArray();
        Check(fc_core_layers(this, ids, (nuint)ids.Length, positions, (nuint)positions.Length, data.Array, (nuint)data.Count));
        RememberVisuals(additions);
    }

    internal ulong[] Delta(bool forward)
    {
        CheckSize(fc_core_delta(this, forward ? 1 : 0, null, 0, out var count));
        if (count == 0) return [];
        var result = new ulong[checked((int)count)];
        Check(fc_core_delta(this, forward ? 1 : 0, result, count, out _));
        return result;
    }

    internal DrawingAttributes ReadBrush(ulong id)
    {
        var prefix = new byte[74];
        Check(fc_core_brush(this, id, prefix, (nuint)prefix.Length, out _));
        using var reader = new BinaryReader(new MemoryStream(prefix, false));
        if (reader.ReadByte() != 1) throw new InvalidDataException("Unknown native brush.");
        return NativeStrokeCodec.ReadBrush(reader);
    }
    internal bool VisualMatches(Stroke stroke) => identities.TryGetValue(stroke, out var entry) &&
        entry.RenderedVersion != 0 && entry.RenderedVersion == RecordVersion(entry.Value);

    internal void Remember(Stroke stroke)
    {
        if (identities.TryGetValue(stroke, out var entry)) entry.RenderedVersion = RecordVersion(entry.Value);
    }

    private void RememberVisuals(IEnumerable<Stroke> strokes)
    {
        if (strokes != null) foreach (var stroke in strokes) Remember(stroke);
    }

    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_versions(NativeInkPage core, [Out] ulong[] dest, nuint count);

    internal ulong RecordVersion(ulong id)
    {
        Check(fc_core_record_version(this, id, out ulong version));
        return version;
    }

    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_load_document(NativeInkPage core, byte[] data, nuint length, ulong firstId, int undoable);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_position(NativeInkPage core, ulong id, out nuint position);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_layers(NativeInkPage core, ulong[] removed, nuint nr, ulong[] positions, nuint ni, byte[] data, nuint length);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_update(NativeInkPage core, byte[] records, nuint n, byte[] brushes, nuint b);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_delta(NativeInkPage core, int forward, [Out] ulong[] dest, nuint capacity, out nuint required);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_brush(NativeInkPage core, ulong id, [Out] byte[] dest, nuint capacity, out nuint required);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_record_version(NativeInkPage core, ulong id, out ulong version);
}
