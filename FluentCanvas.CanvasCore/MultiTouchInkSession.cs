using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Threading;

namespace FluentCanvas.Helpers;

internal readonly record struct TouchEraser(InkCanvasEditingMode Mode, double Size)
{
    internal void Apply(InkCanvas canvas)
    {
        canvas.EraserShape = new EllipseStylusShape(Size, Size);
        canvas.EditingMode = Mode;
    }
}

// Owns the captures and temporary native editing modes for one enabled multi-touch session.
internal sealed class MultiTouchInkSession : IDisposable
{
    private readonly InkCanvas canvas;
    private readonly Window window;
    private readonly HashSet<int> activeTouches;
    private readonly Func<bool> penToolSelected;
    private readonly Func<bool> eraserSelected;
    private readonly MultiTouchInkPreview preview;
    private readonly Dictionary<int, TouchDevice> capturedTouches = new();
    private StylusDevice pen;
    private TouchDevice eraser;
    private DispatcherOperation pendingEraserRestore;
    private bool nativeInkSuspended;
    private bool disposed;

    internal MultiTouchInkSession(InkCanvas canvas, Window window, HashSet<int> activeTouches,
        Func<bool> penToolSelected, Func<bool> eraserSelected)
    {
        this.canvas = canvas;
        this.window = window;
        this.activeTouches = activeTouches;
        this.penToolSelected = penToolSelected;
        this.eraserSelected = eraserSelected;
        preview = new MultiTouchInkPreview(canvas);
        canvas.PreviewStylusDown += OnStylusDown;
        canvas.PreviewStylusUp += OnStylusUp;
        canvas.PreviewTouchMove += OnTouchMove;
        canvas.LostTouchCapture += OnLostCapture;
        canvas.TouchLeave += OnTouchLeave;
        canvas.Unloaded += OnUnloaded;
        window.Deactivated += OnDeactivated;
    }

    internal bool AcceptsInk => penToolSelected() &&
        (canvas.EditingMode == InkCanvasEditingMode.Ink ||
         nativeInkSuspended && canvas.EditingMode == InkCanvasEditingMode.None);

    private bool PenInStroke => pen?.Captured == canvas && !pen.InAir;

    internal void Begin(TouchEventArgs e, TouchEraser? touchEraser)
    {
        var device = e.TouchDevice;
        if (touchEraser is { } tool)
        {
            if (PenInStroke || capturedTouches.Count > 0)
            {
                e.Handled = true;
                return;
            }
            eraser = device;
            nativeInkSuspended = false;
            tool.Apply(canvas);
            return;
        }

        SuspendNativeInk();
        preview.Begin(device.Id, GetPoints(e));
        capturedTouches[device.Id] = device;
        if (!canvas.CaptureTouch(device) || device.Captured != canvas) EndContact(device, complete: false);
        e.Handled = true;
    }

    internal void Complete(TouchEventArgs e, Action<Stroke> strokeCollected)
    {
        var device = e.TouchDevice;
        try
        {
            if (!Owns(device)) return;
            e.Handled = true;
            if (AcceptsInk)
            {
                if (preview.Complete(device.Id, GetPoints(e)) is { } stroke) strokeCollected(stroke);
            }
            else preview.Cancel(device.Id);
        }
        finally
        {
            EndContact(device, complete: true);
        }
    }

    private void OnStylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice.Type == TabletDeviceType.Stylus)
        {
            pen = e.StylusDevice;
            RestoreNativeInk();
        }
        else if (AcceptsInk) SuspendNativeInk();
    }

    private void OnStylusUp(object sender, StylusEventArgs e)
    {
        if (ReferenceEquals(e.StylusDevice, pen)) pen = null;
        if (e.StylusDevice.TabletDevice.Type != TabletDeviceType.Touch ||
            !nativeInkSuspended || activeTouches.Count > 0) return;
        canvas.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!disposed && activeTouches.Count == 0) RestoreNativeInk();
        }));
    }

    private void SuspendNativeInk()
    {
        if (canvas.EditingMode != InkCanvasEditingMode.Ink || PenInStroke) return;
        nativeInkSuspended = true;
        canvas.EditingMode = InkCanvasEditingMode.None;
    }

    private void RestoreNativeInk()
    {
        if (!nativeInkSuspended) return;
        nativeInkSuspended = false;
        if (canvas.EditingMode == InkCanvasEditingMode.None && !eraserSelected())
            canvas.EditingMode = InkCanvasEditingMode.Ink;
    }

    private StylusPointCollection GetPoints(TouchEventArgs e)
    {
        var points = new StylusPointCollection();
        foreach (var sample in e.GetIntermediateTouchPoints(canvas))
            points.Add(new StylusPoint(sample.Position.X, sample.Position.Y));
        var position = e.GetTouchPoint(canvas).Position;
        if (points.Count == 0 || points[^1].X != position.X || points[^1].Y != position.Y)
            points.Add(new StylusPoint(position.X, position.Y));
        return points;
    }

    private bool Owns(TouchDevice device) =>
        capturedTouches.TryGetValue(device.Id, out var current) && ReferenceEquals(current, device);

    private void OnTouchMove(object sender, TouchEventArgs e)
    {
        if (!Owns(e.TouchDevice)) return;
        e.Handled = true;
        if (AcceptsInk) preview.Append(e.TouchDevice.Id, GetPoints(e));
    }

    private void OnLostCapture(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Captured != canvas) EndContact(e.TouchDevice, complete: false);
    }

    private void OnTouchLeave(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Captured == null) EndContact(e.TouchDevice, complete: false);
    }

    private void EndContact(TouchDevice device, bool complete)
    {
        if (capturedTouches.TryGetValue(device.Id, out var current))
        {
            if (!ReferenceEquals(current, device)) return;
            capturedTouches.Remove(device.Id);
            preview.Cancel(device.Id);
            if (device.Captured == canvas) canvas.ReleaseTouchCapture(device);
        }
        if (eraser?.Id == device.Id && !ReferenceEquals(device, eraser)) return;
        activeTouches.Remove(device.Id);
        if (ReferenceEquals(device, eraser))
        {
            eraser = null;
            if (complete)
                pendingEraserRestore = canvas.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(RestoreEraser));
            else RestoreEraser();
        }
        if (capturedTouches.Count == 0) RestoreNativeInk();
    }

    private void RestoreEraser()
    {
        pendingEraserRestore = null;
        if (eraser == null && !eraserSelected() &&
            canvas.EditingMode is InkCanvasEditingMode.EraseByPoint or InkCanvasEditingMode.EraseByStroke)
            canvas.EditingMode = InkCanvasEditingMode.Ink;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Cancel();
    private void OnDeactivated(object sender, EventArgs e) => Cancel();

    internal void Cancel()
    {
        preview.CancelAll();
        var devices = capturedTouches.Values.ToArray();
        capturedTouches.Clear();
        foreach (var device in devices)
        {
            if (device.Captured == canvas) canvas.ReleaseTouchCapture(device);
        }
        if (eraser != null || pendingEraserRestore != null)
        {
            pendingEraserRestore?.Abort();
            eraser = null;
            RestoreEraser();
        }
        pen = null;
        RestoreNativeInk();
        activeTouches.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        canvas.PreviewStylusDown -= OnStylusDown;
        canvas.PreviewStylusUp -= OnStylusUp;
        canvas.PreviewTouchMove -= OnTouchMove;
        canvas.LostTouchCapture -= OnLostCapture;
        canvas.TouchLeave -= OnTouchLeave;
        canvas.Unloaded -= OnUnloaded;
        window.Deactivated -= OnDeactivated;
        Cancel();
    }
}
