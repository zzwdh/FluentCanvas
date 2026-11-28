using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FluentCanvas.Helpers;

[StructLayout(LayoutKind.Sequential)]
internal struct SceneObject
{
    internal Guid Id;
    internal ulong Order;
    internal uint Kind;
    internal int Z;
    internal double Width, Height, Left, Top, Right, Bottom, Opacity, OriginX, OriginY;
    private double m11, m12, m21, m22, dx, dy;

    internal Matrix Matrix
    {
        readonly get => new(m11, m12, m21, m22, dx, dy);
        set => (m11, m12, m21, m22, dx, dy) = (value.M11, value.M12, value.M21, value.M22, value.OffsetX, value.OffsetY);
    }

    private sealed record Identity(Guid Id, ulong Order);
    private static readonly ConditionalWeakTable<UIElement, Identity> identities = new();
    private static long nextOrder;
    private static ulong NewOrder() => checked((ulong)Interlocked.Increment(ref nextOrder));
    private static Identity IdentityFor(UIElement item) => identities.GetValue(item, _ => new(Guid.NewGuid(), NewOrder()));
    internal static Guid ObjectId(UIElement item) => IdentityFor(item).Id;
    internal static void Bind(UIElement item, Guid id)
    {
        if (id != Guid.Empty) identities.Add(item, new Identity(id, NewOrder()));
    }

    internal static SceneObject Capture(UIElement item, Transform transform = null)
    {
        var identity = IdentityFor(item);
        var element = item as FrameworkElement;
        return new SceneObject
        {
            Id = identity.Id,
            Order = identity.Order,
            Kind = item is MediaElement ? 2u : item is Image ? 1u : 3u,
            Z = Panel.GetZIndex(item),
            Width = element?.Width ?? double.NaN,
            Height = element?.Height ?? double.NaN,
            Left = InkCanvas.GetLeft(item),
            Top = InkCanvas.GetTop(item),
            Right = InkCanvas.GetRight(item),
            Bottom = InkCanvas.GetBottom(item),
            Opacity = item.Opacity,
            OriginX = item.RenderTransformOrigin.X,
            OriginY = item.RenderTransformOrigin.Y,
            Matrix = (transform ?? item.RenderTransform)?.Value ?? Matrix.Identity
        };
    }

    internal readonly void Project(UIElement item)
    {
        if (item is FrameworkElement element)
        {
            element.Width = Width;
            element.Height = Height;
        }
        Panel.SetZIndex(item, Z);
        InkCanvas.SetLeft(item, Left);
        InkCanvas.SetTop(item, Top);
        InkCanvas.SetRight(item, Right);
        InkCanvas.SetBottom(item, Bottom);
        item.Opacity = Opacity;
        item.RenderTransformOrigin = new Point(OriginX, OriginY);
        if (item.RenderTransform?.Value != Matrix)
            item.SetCurrentValue(UIElement.RenderTransformProperty, new MatrixTransform(Matrix));
    }

    internal readonly void Attach(InkCanvas canvas, UIElement item)
    {
        Project(item);
        if (canvas.Children.Contains(item)) return;
        int index = 0;
        while (index < canvas.Children.Count && IdentityFor(canvas.Children[index]).Order < Order) index++;
        canvas.Children.Insert(index, item);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SceneChange
{
    internal SceneObject Before, After;
    internal static SceneChange Insert(UIElement item) => new() { After = SceneObject.Capture(item) };
    internal static SceneChange Remove(UIElement item) => new() { Before = SceneObject.Capture(item) };
    internal static SceneChange Move(GestureMediaChange change) => new()
    {
        Before = change.Inserted ? default : SceneObject.Capture(change.Element, change.Before),
        After = SceneObject.Capture(change.Element, change.After)
    };
}

internal sealed partial class NativeInkPage
{
    internal Guid PageId
    {
        get { Guid id = default; Check(fc_scene_page_id(this, ref id, 0)); return id; }
    }

    internal void SetPageId(Guid id) => Check(fc_scene_page_id(this, ref id, 1));

    internal Dictionary<Guid, SceneObject> ReadScene()
    {
        CheckSize(fc_scene_objects(this, null, 0, out var count));
        if (count == 0) return null;
        var objects = new SceneObject[checked((int)count)];
        Check(fc_scene_objects(this, objects, count, out _));
        return objects.ToDictionary(item => item.Id);
    }

    internal void EditScene(SceneChange[] changes, Action editInk)
    {
        if (changes == null || changes.Length == 0) { editInk(); return; }
        Check(fc_scene_prepare(this, changes, (nuint)changes.Length));
        try { editInk(); }
        finally { Check(fc_scene_cancel(this)); }
    }

    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_scene_prepare(NativeInkPage core, SceneChange[] changes, nuint count);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_scene_objects(NativeInkPage core, [Out] SceneObject[] objects, nuint size, out nuint required);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_scene_cancel(NativeInkPage core);
    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_scene_page_id(NativeInkPage core, ref Guid id, int set);
}
