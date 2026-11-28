using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Ink;

namespace FluentCanvas.Helpers;

internal static class InkProjection
{
    // Keep references held by selections, shape tools and element histories. Reinsert a
    // restored stroke at the native layer index instead of moving it above later ink.
    internal static void Restore(StrokeCollection view, NativeInkPage page, Dictionary<ulong, Stroke> available,
        HashSet<ulong> changed)
    {
        var order = page.Order();
        var needed = order.ToHashSet();
        var attached = new HashSet<ulong>();
        for (int i = view.Count - 1; i >= 0; i--)
        {
            var stroke = view[i];
            ulong id = NativeInkPage.Id(stroke);
            if (needed.Contains(id))
            {
                available[id] = stroke;
                attached.Add(id);
            }
            else view.RemoveAt(i);
        }

        for (int i = 0; i < order.Length; i++)
        {
            ulong id = order[i];
            bool inPlace = i < view.Count && NativeInkPage.Id(view[i]) == id;
            if (inPlace && changed != null && !changed.Contains(id)) continue;

            if (!available.TryGetValue(id, out var visual))
            {
                visual = page.Read(id);
            }
            else if (!page.VisualMatches(visual))
            {
                var stored = page.Read(id);
                CopyContent(visual, stored);
                page.Remember(visual);
            }
            if (inPlace) continue;
            if (attached.Contains(id)) view.Remove(visual);
            view.Insert(i, visual);
        }
    }
    internal static void ApplyDelta(StrokeCollection view, NativeInkPage page, Dictionary<ulong, Stroke> available, ulong[] operations)
    {
        for (int j = 0; j < operations.Length; j += 3)
        {
            ulong kind = operations[j], id = operations[j + 2];
            int index = checked((int)operations[j + 1]);
            if (kind == 2)
            {
                if (!available.TryGetValue(id, out var visual)) visual = page.Read(id);
                else if (!page.VisualMatches(visual))
                {
                    CopyContent(visual, page.Read(id));
                    page.Remember(visual);
                }
                view.Insert(index, visual);
                continue;
            }
            if (index < 0 || index >= view.Count || NativeInkPage.Id(view[index]) != id)
                throw new InvalidOperationException("Ink view and native edit order diverged.");
            var active = view[index];
            switch (kind)
            {
                case 1:
                    available[id] = active;
                    view.RemoveAt(index);
                    break;
                case 3:
                    CopyContent(active, page.Read(id));
                    page.Remember(active);
                    break;
                case 4:
                    active.DrawingAttributes = page.ReadBrush(id);
                    page.Remember(active);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported ink edit.");
            }
        }
    }

    private static void CopyContent(Stroke visual, Stroke stored)
    {
        visual.StylusPoints = stored.StylusPoints;
        visual.DrawingAttributes = stored.DrawingAttributes;
        foreach (var previousId in visual.GetPropertyDataIds())
            if (!stored.ContainsPropertyData(previousId)) visual.RemovePropertyData(previousId);
        foreach (var propertyId in stored.GetPropertyDataIds())
            visual.AddPropertyData(propertyId, stored.GetPropertyData(propertyId));
    }
}
