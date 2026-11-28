using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;

namespace FluentCanvas.Helpers;

// Capture on the tunneling event before InkCanvas starts its built-in editing
// behavior. Promoted mouse events must not open a second eraser transaction.
internal sealed class IndexedEraseInput
{
    private readonly InkCanvas canvas;
    private readonly Action<double, bool> begin;
    private readonly Action<Point> sweep;
    private readonly Action finish;
    private InputDevice owner;

    internal IndexedEraseInput(Window window, InkCanvas canvas, Action<double, bool> begin,
        Action<Point> sweep, Action finish)
    {
        this.canvas = canvas;
        this.begin = begin;
        this.sweep = sweep;
        this.finish = finish;
        bool OnCanvas(object source) => source == canvas || source is Visual visual && canvas.IsAncestorOf(visual);
        window.PreviewMouseDown += (_, e) =>
        {
            if (OnCanvas(e.OriginalSource) && e.StylusDevice == null && e.ChangedButton == MouseButton.Left)
                e.Handled |= Start(e.MouseDevice, e.GetPosition(canvas), canvas.EditingMode);
        };
        canvas.PreviewMouseMove += (_, e) =>
        {
            if (owner != e.MouseDevice) return;
            e.Handled = true;
            sweep(e.GetPosition(canvas));
        };
        canvas.PreviewMouseUp += (_, e) =>
        {
            if (owner != e.MouseDevice || e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            EndAt(e.GetPosition(canvas));
        };
        window.PreviewStylusDown += (_, e) =>
        {
            if (!OnCanvas(e.OriginalSource) || e.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch) return;
            e.Handled |= Start(e.StylusDevice, e.GetPosition(canvas),
                e.StylusDevice.Inverted ? canvas.EditingModeInverted : canvas.EditingMode);
        };
        canvas.PreviewStylusMove += (_, e) =>
        {
            if (owner != e.StylusDevice) return;
            e.Handled = true;
            foreach (var point in e.GetStylusPoints(canvas)) sweep(point.ToPoint());
        };
        canvas.PreviewStylusUp += (_, e) =>
        {
            if (owner != e.StylusDevice) return;
            e.Handled = true;
            EndAt(e.GetPosition(canvas));
        };
        window.PreviewTouchDown += (_, e) =>
        {
            if (OnCanvas(e.OriginalSource))
                e.Handled |= Start(e.TouchDevice, e.GetTouchPoint(canvas).Position, canvas.EditingMode);
        };
        window.PreviewTouchMove += (_, e) =>
        {
            if (owner != e.TouchDevice) return;
            e.Handled = true;
            foreach (var point in e.GetIntermediateTouchPoints(canvas)) sweep(point.Position);
            sweep(e.GetTouchPoint(canvas).Position);
        };
        window.PreviewTouchUp += (_, e) =>
        {
            if (owner != e.TouchDevice) return;
            e.Handled = true;
            EndAt(e.GetTouchPoint(canvas).Position);
        };
        canvas.LostMouseCapture += (_, e) => { if (owner == e.MouseDevice) Complete(); };
        canvas.LostStylusCapture += (_, e) => { if (owner == e.StylusDevice) Complete(); };
        canvas.LostTouchCapture += (_, e) => { if (owner == e.TouchDevice) Complete(); };
        canvas.Unloaded += (_, _) => Complete();
        canvas.EditingModeChanged += (_, _) => Complete();
        window.Deactivated += (_, _) => Complete();
    }

    private bool Start(InputDevice device, Point point, InkCanvasEditingMode mode)
    {
        if (mode is not (InkCanvasEditingMode.EraseByPoint or InkCanvasEditingMode.EraseByStroke) ||
            canvas.EraserShape is not EllipseStylusShape shape || shape.Width != shape.Height) return false;
        if (owner != null) return true;
        bool captured = device switch
        {
            MouseDevice mouse => mouse.Capture(canvas),
            StylusDevice stylus => stylus.Capture(canvas),
            TouchDevice touch => touch.Capture(canvas),
            _ => false
        };
        if (!captured) return false;
        try
        {
            begin(shape.Width, mode == InkCanvasEditingMode.EraseByStroke);
            owner = device;
            sweep(point);
            return true;
        }
        catch
        {
            owner = device;
            Complete();
            throw;
        }
    }

    private void EndAt(Point point)
    {
        try { sweep(point); }
        finally { Complete(); }
    }

    internal void Complete()
    {
        var captured = owner;
        if (captured == null) return;
        owner = null;
        try { finish(); }
        finally
        {
            switch (captured)
            {
                case MouseDevice m when m.Captured == canvas: m.Capture(null); break;
                case StylusDevice s when s.Captured == canvas: s.Capture(null); break;
                case TouchDevice t when t.Captured == canvas: t.Capture(null); break;
            }
        }
    }
}
