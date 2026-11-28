using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;

namespace FluentCanvas.Helpers;

internal sealed partial class NativeInkPage
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Cut(ulong Id, double Start, double End);

    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_erase_query(NativeInkPage page, double x0, double y0, double x1, double y1,
        double radius, [Out] Cut[] cuts, nuint capacity, out nuint required);

    internal int QueryErase(Point from, Point to, double radius, ref Cut[] result)
    {
        int Query(Cut[] buffer, out nuint needed) => fc_core_erase_query(this, from.X, from.Y,
            to.X, to.Y, radius, buffer, (nuint)buffer.Length, out needed);
        int status = Query(result, out var count);
        if (status == 3)
        {
            Array.Resize(ref result, checked((int)count));
            status = Query(result, out count);
        }
        Check(status);
        return checked((int)count);
    }
}

public partial class TimeMachine
{
    internal IndexedInkErase StartErase(StrokeCollection view, double diameter, bool wholeStroke) =>
        new(page, view, diameter, wholeStroke);
}

// Rust queries the frozen geometry of the active revision. Each preview fragment
// keeps its interval in the original stroke so successive cuts never resample
// an already interpolated endpoint. The final replacements form one undo step.
internal sealed class IndexedInkErase
{
    private readonly record struct Part(Stroke Stroke, double Start, double End);
    private sealed record Edited(Stroke Original, List<Part> Parts);
    private readonly Dictionary<ulong, Edited> changed = new();
    private readonly NativeInkPage page;
    private readonly StrokeCollection view;
    private readonly double radius;
    private readonly bool wholeStroke;
    private readonly StylusShape eraser;
    private readonly Dictionary<ulong, Stroke> live;
    private NativeInkPage.Cut[] cuts = new NativeInkPage.Cut[32];
    private static readonly ConditionalWeakTable<StrokeCollection, Dictionary<ulong, Stroke>> projection = new();

    internal IndexedInkErase(NativeInkPage page, StrokeCollection view, double diameter, bool wholeStroke)
    {
        this.page = page;
        this.view = view;
        this.wholeStroke = wholeStroke;
        radius = diameter * 0.5;
        eraser = new EllipseStylusShape(diameter, diameter);
        live = projection.GetValue(view, strokes =>
        {
            var index = strokes.ToDictionary(NativeInkPage.Id);
            strokes.StrokesChanged += (_, e) =>
            {
                foreach (var stroke in e.Removed) index.Remove(NativeInkPage.Id(stroke));
                foreach (var stroke in e.Added) index[NativeInkPage.Id(stroke)] = stroke;
            };
            return index;
        });
    }

    internal void Sweep(Point from, Point to)
    {
        int count = page.QueryErase(from, to, radius, ref cuts);
        for (int i = 0; i < count;)
        {
            int first = i++;
            ulong id = cuts[first].Id;
            while (i < count && cuts[i].Id == id) i++;
            if (!changed.TryGetValue(id, out var edit))
            {
                if (!live.TryGetValue(id, out var original)) continue;
                edit = new Edited(original, [new Part(original, 0, original.StylusPoints.Count - 1)]);
            }
            if (edit.Parts.Count == 0) continue;
            bool outline = cuts[first].Start < 0;
            for (int j = edit.Parts.Count - 1; j >= 0; j--)
            {
                var part = edit.Parts[j];
                List<Part> remaining;
                if (outline)
                {
                    Point[] path = from == to ? [from] : [from, to];
                    if (!part.Stroke.HitTest(path, eraser)) continue;
                    remaining = wholeStroke ? [] : part.Stroke.GetEraseResult(path, eraser).Select(fragment =>
                    {
                        RemoveGeometry(fragment);
                        return new Part(fragment, 0, fragment.StylusPoints.Count - 1);
                    }).ToList();
                }
                else if (wholeStroke || edit.Original.StylusPoints.Count == 1) remaining = [];
                else
                {
                    remaining = Subtract(part, edit.Original, cuts.AsSpan(first, i - first));
                    if (remaining == null) continue;
                }
                changed.TryAdd(id, edit);
                if (remaining.Count == 0) view.Remove(part.Stroke);
                else view.Replace(part.Stroke, new StrokeCollection(remaining.Select(p => p.Stroke)));
                edit.Parts.RemoveAt(j);
                edit.Parts.InsertRange(j, remaining);
            }
        }
    }

    private static List<Part> Subtract(Part part, Stroke original, ReadOnlySpan<NativeInkPage.Cut> intervals)
    {
        List<Part> remaining = null;
        double cursor = part.Start;
        foreach (var cut in intervals)
        {
            double begin = Math.Max(cursor, cut.Start), end = Math.Min(part.End, cut.End);
            if (end <= begin) continue;
            remaining ??= new();
            if (begin > cursor) remaining.Add(Slice(original, cursor, begin));
            cursor = end;
        }
        if (remaining != null && cursor < part.End) remaining.Add(Slice(original, cursor, part.End));
        return remaining;
    }

    private static Part Slice(Stroke original, double start, double end)
    {
        var source = original.StylusPoints;
        var points = new StylusPointCollection(source.Description);
        points.Add(Interpolate(source, start));
        for (int i = (int)Math.Floor(start) + 1; i < end; i++) points.Add(source[i]);
        points.Add(Interpolate(source, end));
        var result = new Stroke(points, original.DrawingAttributes.Clone());
        foreach (var id in original.GetPropertyDataIds())
            if (id != NativeStrokeCodec.ShapeProperty && id != ParametricInk.GeometryProperty)
                result.AddPropertyData(id, original.GetPropertyData(id));
        return new Part(result, start, end);
    }

    private static void RemoveGeometry(Stroke fragment)
    {
        if (fragment.ContainsPropertyData(ParametricInk.GeometryProperty)) fragment.RemovePropertyData(ParametricInk.GeometryProperty);
        if (fragment.ContainsPropertyData(NativeStrokeCodec.ShapeProperty)) fragment.RemovePropertyData(NativeStrokeCodec.ShapeProperty);
    }

    private static StylusPoint Interpolate(StylusPointCollection points, double index)
    {
        int floor = (int)Math.Floor(index);
        var a = points[floor];
        double t = index - floor;
        if (t == 0) return a;
        var b = points[floor + 1];
        var description = points.Description;
        var channels = description.GetStylusPointProperties();
        int[] extra = new int[channels.Count - 3];
        for (int i = 3; i < channels.Count; i++)
        {
            var channel = channels[i];
            int left = a.GetPropertyValue(channel), right = b.GetPropertyValue(channel);
            extra[i-3] = channel.IsButton ? (t < 0.5 ? left : right) :
                (int)Math.Round(left + ((double)right - left) * t);
        }
        return new StylusPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
            (float)(a.PressureFactor + ((double)b.PressureFactor - a.PressureFactor) * t), description, extra);
    }

    internal void Finish(TimeMachine history)
    {
        if (changed.Count == 0) return;
        var old = new StrokeCollection(changed.Values.Select(x => x.Original));
        var pieces = new StrokeCollection(changed.Values.SelectMany(x => x.Parts.Select(p => p.Stroke)));
        history.CommitStrokeEraseHistory(old, pieces);
        changed.Clear();
    }

    internal void RestoreProjection()
    {
        var available = changed.Values.Select(edit => edit.Original).ToDictionary(NativeInkPage.Id);
        InkProjection.Restore(view, page, available, changed: null);
        changed.Clear();
    }
}
