using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace FluentCanvas;

public partial class MainWindow
{
    private ShapeAnchorIndex shapeSnapPoints;
    private Stroke shapeHandleStroke;
    private DispatcherOperation pendingShapeHandleRefresh;
    private bool draggingShapeHandle;

    private ShapeAnchorIndex CollectShapeSnapPoints(HashSet<Stroke> excluded = null)
    {
        var points = new ShapeAnchorIndex();
        foreach (var stroke in inkCanvas.Strokes)
        {
            if (excluded?.Contains(stroke) == true || ParametricInk.Get(stroke) is not { } geometry) continue;
            foreach (var point in geometry.SnapAnchors()) points.Add(point);
        }
        return points;
    }

    private Point SnapShapePoint(Point input) =>
        (shapeSnapPoints ??= CollectShapeSnapPoints()).Snap(input);

    private void RefreshShapeHandles()
    {
        if (ShapeHandles == null || draggingShapeHandle) return;
        if (shapeHandleStroke != null) shapeHandleStroke.PropertyDataChanged -= OnHandleGeometryChanged;
        shapeHandleStroke = null;
        ShapeHandles.Children.Clear();
        if (!GridInkCanvasSelectionCover.IsVisible || inkCanvas.GetSelectedElements().Count > 0) return;
        var selected = inkCanvas.GetSelectedStrokes();
        if (selected.Count == 0 || ParametricInk.Get(selected[0]) is not { } shape) return;
        if (selected.Count > 1 && (shape.Group == Guid.Empty ||
            selected.Skip(1).Any(s => ParametricInk.Get(s) is not { } other || !SameShapeFrame(shape, other)))) return;
        var stroke = shapeHandleStroke = selected[0];
        stroke.PropertyDataChanged += OnHandleGeometryChanged;

        for (int i = 0; i < shape.HandleCount; i++)
        {
            int index = i;
            var thumb = new Thumb { Width = 14, Height = 14, Cursor = Cursors.SizeAll,
                Background = SystemColors.HighlightBrush, BorderBrush = SystemColors.HighlightTextBrush,
                BorderThickness = new Thickness(1), Focusable = true };
            string label = shape.Kind switch { GeometryKind.Triangle => "Triangle", GeometryKind.NumberLine => "NumberLineTicks",
                GeometryKind.Axis => "Axis1", GeometryKind.Arc when Math.Abs(shape.Parameter) >= Math.Tau => "Ellipse", _ => "Arc" };
            AutomationProperties.SetName(thumb, UiText.Get("Canvas." + label) + " " + (i + 1));
            AutomationProperties.SetAutomationId(thumb, "GeometryHandle" + i);
            var origin = shape.Handle(i);
            Point target = origin;
            Stroke[] edited = [];
            ShapeAnchorIndex snap = null;
            ulong revision = 0;
            void Start()
            {
                CommitPendingCanvasEdits();
                revision = timeMachine.CurrentState;
                edited = shape.Group == Guid.Empty ? [stroke] : inkCanvas.Strokes
                    .Where(s => ParametricInk.Get(s) is { } g && SameShapeFrame(shape, g)).ToArray();
                snap = CollectShapeSnapPoints(edited.ToHashSet());
            }
            void Move(Point value) => target = snap.Snap(value);
            thumb.DragStarted += (_, _) => { Start(); draggingShapeHandle = true; target = origin; };
            thumb.DragDelta += (_, e) =>
            {
                if (!draggingShapeHandle || revision != timeMachine.CurrentState) return;
                Move(thumb.TranslatePoint(new Point(thumb.Width / 2 + e.HorizontalChange,
                    thumb.Height / 2 + e.VerticalChange), inkCanvas));
                PlaceHandle(thumb, target);
                var preview = new StrokeCollection(edited.Select(s =>
                    ParametricInk.Get(s).WithHandle(index, target).Create(s.DrawingAttributes)));
                inkCanvas.SetShapePreview(null, preview);
            };
            thumb.DragCompleted += (_, e) =>
            {
                draggingShapeHandle = false;
                inkCanvas.SetShapePreview(null, null);
                if (!e.Canceled && target != origin && revision == timeMachine.CurrentState && edited.All(inkCanvas.Strokes.Contains))
                    EditShapeHandle(edited, index, target);
                RefreshShapeHandles();
            };
            thumb.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && thumb.IsDragging)
                {
                    thumb.CancelDrag();
                    e.Handled = true;
                    return;
                }
                var delta = e.Key switch { Key.Left => new Vector(-1,0), Key.Right => new Vector(1,0),
                    Key.Up => new Vector(0,-1), Key.Down => new Vector(0,1), _ => default };
                if (delta == default) return;
                e.Handled = true;
                Start();
                EditShapeHandle(edited, index, ParametricInk.Get(stroke).Handle(index) + delta);
            };
            ShapeHandles.Children.Add(thumb);
            PlaceHandle(thumb, origin);
        }
    }

    private static bool SameShapeFrame(ParametricInk a, ParametricInk b) =>
        a.Group == b.Group && a.Kind == b.Kind && a.A == b.A && a.B == b.B && a.C == b.C;

    private void PlaceHandle(Thumb thumb, Point point)
    {
        Point local = inkCanvas.TranslatePoint(point, ShapeHandles);
        System.Windows.Controls.Canvas.SetLeft(thumb, local.X - thumb.Width / 2);
        System.Windows.Controls.Canvas.SetTop(thumb, local.Y - thumb.Height / 2);
    }

    private void OnHandleGeometryChanged(object sender, PropertyDataChangedEventArgs e)
    {
        if (e.PropertyGuid != ParametricInk.GeometryProperty || draggingShapeHandle) return;
        pendingShapeHandleRefresh ??= Dispatcher.InvokeAsync(() =>
        {
            pendingShapeHandleRefresh = null;
            RefreshShapeHandles();
        });
    }

    private void EditShapeHandle(Stroke[] strokes, int index, Point position)
    {
        var before = strokes.Select(ParametricInk.Get).ToArray();
        var previous = _currentCommitType;
        _currentCommitType = CommitReason.CodeInput;
        try
        {
            for (int i = 0; i < strokes.Length; i++) before[i].WithHandle(index, position).Apply(strokes[i]);
            timeMachine.CommitStrokeManipulationHistory(strokes, null);
        }
        catch
        {
            for (int i = 0; i < strokes.Length; i++) before[i].Apply(strokes[i]);
            throw;
        }
        finally { _currentCommitType = previous; }
    }
}

