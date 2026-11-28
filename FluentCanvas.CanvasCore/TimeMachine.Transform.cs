using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentCanvas.Helpers;

internal sealed partial class NativeInkPage
{
    internal ulong[] Transform(CanvasGesturePreview gesture)
    {
        if (gesture.Strokes.Length == 0)
        {
            Append(null);
            return [];
        }
        var matrix = gesture.Matrix;
        var width = gesture.Width;
        var height = gesture.Height;
        double[] values = [
            matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.OffsetX, matrix.OffsetY,
            width.Factor, width.Low, width.High, height.Factor, height.Low, height.High,
            StylusPoint.MinXY, StylusPoint.MaxXY
        ];
        var ids = gesture.Strokes.Select(Id).ToArray();
        var versions = new ulong[ids.Length];
        Check(fc_core_place(this, ids, (nuint)ids.Length, values, versions));
        return versions;
    }

    [DllImport(Library, CallingConvention = Abi)]
    private static extern int fc_core_place(NativeInkPage page, ulong[] ids, nuint count, double[] matrix, [Out] ulong[] versions);

    internal void ProjectGesture(CanvasGesturePreview gesture, ulong[] versions)
    {
        var matrix = gesture.Matrix;
        for (int index = 0; index < versions.Length; index++)
        {
            var stroke = gesture.Strokes[index];
            if (versions[index] == 0 || !matrix.HasInverse)
            {
                var stored = Read(Id(stroke));
                stroke.StylusPoints = stored.StylusPoints;
                stroke.DrawingAttributes = stored.DrawingAttributes;
                ParametricInk.CopyGeometry(stroke, stored);
                Remember(stroke);
                continue;
            }
            if (ParametricInk.Get(stroke) is { } shape) shape.Transformed(matrix).Apply(stroke);
            else if (!matrix.IsIdentity) stroke.Transform(matrix, false);
            var attributes = stroke.DrawingAttributes;
            var width = gesture.Width;
            var height = gesture.Height;
            double w = Math.Clamp(attributes.Width * width.Factor, width.Low, width.High);
            double h = Math.Clamp(attributes.Height * height.Factor, height.Low, height.High);
            if (w != attributes.Width || h != attributes.Height)
            {
                var brush = attributes.Clone();
                brush.Width = w;
                brush.Height = h;
                stroke.DrawingAttributes = brush;
            }
            if (identities.TryGetValue(stroke, out var identity)) identity.RenderedVersion = versions[index];
        }
    }
}

internal sealed record GestureMediaChange(UIElement Element, Transform Before, Transform After, bool Inserted)
{
    internal void Apply(InkCanvas canvas, bool undo)
    {
        if (Inserted && undo)
        {
            canvas.Children.Remove(Element);
            return;
        }
        if (Inserted && !canvas.Children.Contains(Element)) canvas.Children.Add(Element);
        Element.SetCurrentValue(UIElement.RenderTransformProperty, undo ? Before : After);
    }
}

public partial class TimeMachineHistory
{
    internal GestureMediaChange[] GestureMedia { get; init; }
}
public partial class TimeMachine
{
    internal void CommitGesture(CanvasGesturePreview gesture, GestureMediaChange[] elements)
    {
        var item = new TimeMachineHistory(gesture.Strokes, null, TimeMachineHistoryType.Manipulation) { GestureMedia = elements };
        ulong[] versions = null;
        Commit(item, native => versions = native.Transform(gesture));
        page.ProjectGesture(gesture, versions);
    }
}
