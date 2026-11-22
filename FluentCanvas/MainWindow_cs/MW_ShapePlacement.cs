using FluentCanvas.Localization;
using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Ink;
using System.Windows.Input;

namespace FluentCanvas;

public partial class MainWindow
{
    private Point? newShapeFirstPoint;
    private Point? newShapeSecondPoint;
    private StrokeCollection newShapeGuide;
    private TouchDevice newShapeTouch;
    private double? lastArcPointerAngle;
    private double arcSweep;

    private bool UsesNewShapeInput => drawingShapeMode is ShapeTool.Ellipse or >= ShapeTool.Triangle;
    private bool IsThreePointShape => drawingShapeMode is ShapeTool.Triangle or ShapeTool.AngleMark or
        ShapeTool.RightAngleMark or ShapeTool.Arc or ShapeTool.Sector;
    private bool HasNewShapeInProgress => UsesNewShapeInput &&
        (isMouseDown || newShapeTouch != null || newShapeFirstPoint.HasValue);

    private void NewShapeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name } || !Enum.TryParse(name, out ShapeTool shape)) return;
        SelectShapeTool(shape, false);
        RefreshShapePlacementHint();
    }

    private static bool IsTouchPromotedMouse(MouseEventArgs e) =>
        e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch;

    private void BeginNewShape(Point position)
    {
        iniP = position;
        HidePanelsForInking();
        RefreshShapePlacementHint();
        if (newShapeFirstPoint.HasValue) UpdateNewShape(position);
    }

    private void UpdateNewShape(Point position)
    {
        if (drawingShapeMode is ShapeTool.NumberLine or ShapeTool.NumberLineLabeled or ShapeTool.Sphere or ShapeTool.Tetrahedron)
        {
            DrawNewSpatialShape(drawingShapeMode, iniP, position);
            return;
        }

        if (drawingShapeMode == ShapeTool.Ellipse)
        {
            Vector size = position - iniP;
            SetShapeDraft(Math.Abs(size.X) >= MinimumPlaneShapeLength && Math.Abs(size.Y) >= MinimumPlaneShapeLength
                ? new StrokeCollection { CreatePlaneStroke(GenerateEllipseGeometry(iniP, position)) }
                : new StrokeCollection());
            return;
        }

        var draft = newShapeSecondPoint is Point second
            ? CreatePlaneShape(drawingShapeMode, second, position, newShapeFirstPoint, TrackArcSweep(position))
            : CreatePlaneShape(drawingShapeMode, newShapeFirstPoint ?? iniP, position);
        SetShapeDraft(draft);
    }

    private double? TrackArcSweep(Point position)
    {
        if (drawingShapeMode is not (ShapeTool.Arc or ShapeTool.Sector) ||
            newShapeFirstPoint is not Point center || newShapeSecondPoint is not Point start) return null;

        Vector ray = position - center;
        if (ray.Length < MinimumPlaneShapeLength)
        {
            lastArcPointerAngle = null;
            return null;
        }

        double angle = Math.Atan2(ray.Y, ray.X);
        double reference = lastArcPointerAngle ?? Math.Atan2(start.Y - center.Y, start.X - center.X);
        double delta = Math.IEEERemainder(angle - reference, Math.Tau);
        arcSweep = (lastArcPointerAngle.HasValue ? arcSweep : 0) + delta;
        lastArcPointerAngle = angle;
        return arcSweep;
    }

    private void FinishNewShape(Point position)
    {
        UpdateNewShape(position);
        if (!IsThreePointShape || newShapeSecondPoint.HasValue)
        {
            if (shapeDraft is { Count: > 0 }) CompleteShapeDraft();
            return;
        }

        newShapeFirstPoint ??= iniP;
        if (shapeDraft is { Count: > 0 })
        {
            newShapeSecondPoint = position;
            newShapeGuide = shapeDraft;
            shapeDraft = null;
            inkCanvas.SetShapePreview(null, newShapeGuide);
        }
        RefreshShapePlacementHint();
    }

    private void InkCanvas_PreviewShapeTouchDown(object sender, TouchEventArgs e)
    {
        if (!UsesNewShapeInput)
        {
            inkCanvas_PreviewTouchDown(sender, e);
            return;
        }

        e.Handled = true;
        if (newShapeTouch != null || isMouseDown)
        {
            CancelShapeDraft();
            RefreshShapePlacementHint();
            return;
        }

        if (!e.TouchDevice.Capture(inkCanvas)) return;
        newShapeTouch = e.TouchDevice;
        BeginNewShape(e.GetTouchPoint(inkCanvas).Position);
    }

    private void InkCanvas_PreviewNewShapeTouchMove(object sender, TouchEventArgs e)
    {
        if (!UsesNewShapeInput) return;
        e.Handled = true;
        if (ReferenceEquals(newShapeTouch, e.TouchDevice)) UpdateNewShape(e.GetTouchPoint(inkCanvas).Position);
    }

    private void InkCanvas_PreviewShapeTouchUp(object sender, TouchEventArgs e)
    {
        if (!UsesNewShapeInput)
        {
            inkCanvas_PreviewTouchUp(sender, e);
            return;
        }

        e.Handled = true;
        if (!ReferenceEquals(newShapeTouch, e.TouchDevice)) return;
        Point position = e.GetTouchPoint(inkCanvas).Position;
        newShapeTouch = null;
        e.TouchDevice.Capture(null);
        FinishNewShape(position);
    }

    private void InkCanvas_LostShapeTouchCapture(object sender, TouchEventArgs e)
    {
        if (!ReferenceEquals(newShapeTouch, e.TouchDevice)) return;
        CancelShapeDraft();
        RefreshShapePlacementHint();
    }

    private void InkCanvas_LostShapeMouseCapture(object sender, MouseEventArgs e)
    {
        if (!UsesNewShapeInput || !isMouseDown || inkCanvas.IsMouseCaptured) return;
        CancelShapeDraft();
        RefreshShapePlacementHint();
    }

    private void ResetNewShapeInput()
    {
        if (UsesNewShapeInput && isMouseDown && inkCanvas.IsMouseCaptured)
        {
            isMouseDown = false;
            inkCanvas.ReleaseMouseCapture();
        }
        if (newShapeTouch is { } touch)
        {
            newShapeTouch = null;
            if (touch.Captured == inkCanvas) touch.Capture(null);
        }
        newShapeFirstPoint = null;
        newShapeSecondPoint = null;
        newShapeGuide = null;
        lastArcPointerAngle = null;
        if (ShapePlacementHint != null) ShapePlacementHint.Visibility = Visibility.Collapsed;
    }

    private bool StopNewShape()
    {
        if (!UsesNewShapeInput) return false;
        BtnPen_Click(null, null);
        RestoreMultiTouchAfterShape();
        return true;
    }

    private void RestoreMultiTouchAfterShape()
    {
        if (!lastIsInMultiTouchMode) return;
        ToggleSwitchEnableMultiTouchMode.IsOn = true;
        lastIsInMultiTouchMode = false;
    }

    private void RefreshShapePlacementHint()
    {
        bool finalPoint = newShapeSecondPoint.HasValue;
        string key = drawingShapeMode switch
        {
            ShapeTool.Triangle => finalPoint ? "Canvas.PlaceTriangleVertex" : "Canvas.PlaceTriangleFirstSide",
            ShapeTool.AngleMark or ShapeTool.RightAngleMark => finalPoint ? "Canvas.PlaceAngleSecondSide" : "Canvas.PlaceAngleFirstSide",
            ShapeTool.Arc or ShapeTool.Sector => finalPoint ? "Canvas.PlaceArcEnd" : "Canvas.PlaceArcRadius",
            ShapeTool.Semicircle => "Canvas.DrawSemicircle",
            _ => null
        };
        if (key == null) return;
        string hint = UiText.Get(key);
        bool changed = ShapePlacementHintText.Text != hint || ShapePlacementHint.Visibility != Visibility.Visible;
        ShapePlacementHintText.Text = hint;
        ShapePlacementHint.Visibility = Visibility.Visible;
        if (changed && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(ShapePlacementHintText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
