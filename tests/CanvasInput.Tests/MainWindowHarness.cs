using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using FluentCanvas.Helpers;

namespace FluentCanvas;

// The production touch partial is compiled unchanged. Unrelated shell actions are replaced with local stubs.
public partial class MainWindow
{
    private readonly ShapeDraftInkCanvas inkCanvas = new() { Background = Brushes.Transparent, IsManipulationEnabled = true };
    private const int BoundsWidth = 100;
    private bool isHidingSubPanelsWhenInking;
    private readonly bool forceEraser = false;
    private readonly ShapeTool drawingShapeMode = ShapeTool.Pen;
    private bool isFirstTouchCuboid => true;
    private bool IsTwoFingerGestureActive => true;
    private bool IsTwoFingerTranslateActive => true;
    private bool isGridInkCanvasSelectionCoverMouseDown => false;
    private readonly Button BtnPPTSlideShowEnd = new() { Visibility = Visibility.Collapsed };
    private readonly List<Circle> circles = new();

    internal MainWindow()
    {
        Width = 420;
        Height = 300;
        ShowInTaskbar = false;
        ShowActivated = false;
        Opacity = 0;
        Content = inkCanvas;
        inkCanvas.PreviewTouchDown += inkCanvas_PreviewTouchDown;
        inkCanvas.PreviewTouchUp += inkCanvas_PreviewTouchUp;
        inkCanvas.StrokeCollected += inkCanvas_StrokeCollected;
        timeMachine.BindInk(inkCanvas.Strokes);
        inkCanvas.Strokes.StrokesChanged += StrokesOnStrokesChanged;
        inkCanvas.TouchDown += Main_Grid_TouchDown;
        SetMultiTouch(true);
    }

    internal InkCanvas Canvas => inkCanvas;
    internal int CollectedCount { get; private set; }
    internal void ResetCanvasInput() => CancelPendingCanvasInput();
    internal TimeMachine History => timeMachine;
    internal bool HasPendingPointHistory => strokeManipulationHistory.Count > 0;
    internal void BeginBoardGesture()
    {
        gestureCancelled = false;
        dec.Add(100);
    }
    internal void PreviewBoard(Matrix delta, double scaleX = 1, double scaleY = 1) => PreviewBoardGesture(delta, scaleX, scaleY);
    internal void PreviewSelection(Stroke[] strokes, UIElement[] elements, Matrix delta)
    {
        if (contentGesture == null) BeginContentGesture(strokes, elements, true);
        contentGesture.Append(delta, 1, 1);
    }
    internal bool HasGesturePreview => contentGesture != null;
    internal bool HasCommittedPreview => committedPreview != null;
    internal void CancelPreview() => CancelContentGesture();
    internal void TrackBoardTransform(Stroke stroke, Matrix transform) => stroke.Transform(transform, false);

    internal void FinishBoardGesture()
    {
        dec.Clear();
        CommitPendingCanvasEdits();
    }

    internal void AbortBoardGesture()
    {
        ClearStrokes(true);
        timeMachine.ClearStrokeHistory();
    }

    internal void Undo() => ApplyHistoryToCanvas(timeMachine.Undo());
    internal void Redo() => ApplyHistoryToCanvas(timeMachine.Redo());

    internal void SetMultiTouch(bool enabled)
    {
        if (isInMultiTouchMode != enabled) BorderMultiTouchMode_MouseUp(null, null);
    }

    private record AdvancedSettings(double TouchMultiplier = 1, bool IsSpecialScreen = false, bool IsQuadIR = false,
        double NibModeBoundsWidthThresholdValue = 2, double FingerModeBoundsWidthThresholdValue = 2,
        double NibModeBoundsWidthEraserSize = 1, double FingerModeBoundsWidthEraserSize = 1);
    private record StartupSettings(bool IsEnableNibMode = false);
    private record GestureSettings(bool IsEnableTwoFingerZoom = true, bool IsEnableTwoFingerRotation = false);
    private record PowerPointSettings(bool IsEnableTwoFingerGestureInPresentationMode = false, bool IsEnableFingerGestureSlideShowControl = false);
    private record TestSettings(AdvancedSettings Advanced, StartupSettings Startup, GestureSettings Gesture, PowerPointSettings PowerPointSettings);
    private static readonly TestSettings Settings = new(new(), new(), new(), new());

    private sealed class Circle
    {
        public double R { get; set; }
        public Point Centroid { get; set; }
        public Stroke Stroke { get; init; }
    }

    private void HideSubPanels() { }
    private static bool NeedUpdateIniP() => false;
    private void MouseTouchMove(Point point) { }
    private void CancelShapeDraft() { }
    private void InvalidateInkRecognition() { }


    internal void ApplyElementMatrixTransform(UIElement element, Matrix matrix)
    {
        var attached = (FrameworkElement)element;
        var proposed = new TransformGroup();
        proposed.Children.Add(new MatrixTransform(matrix));
        ElementsManipulationHistory ??= new();
        ElementsManipulationHistory[attached.Name] = Tuple.Create<object, TransformGroup>(attached.RenderTransform, proposed);
        attached.RenderTransform = proposed;
    }
    private static Point GetMatrixTransformCenterPoint(Point point, FrameworkElement element) => point;
    private static double GetDistance(Point first, Point second) => (second - first).Length;
    private void inkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e) => CollectedCount++;
}

internal static class FluentCanvasElementsHelper
{
    internal static IEnumerable<UIElement> GetAllElements(InkCanvas canvas) => canvas.Children.Cast<UIElement>();

    internal sealed record ElementData(FrameworkElement FrameworkElement, double SetLeftData, double SetTopData);
}

internal static class LogHelper
{
    internal enum LogType { Error }

    internal static void WriteLogToFile(string message, LogType type) => throw new InvalidOperationException(message);
}

internal sealed class ManagedTouchDevice(InkCanvas canvas, int id, double contactSize = 2) : TouchDevice(id)
{
    private Point _position;
    private TouchAction _action;

    public override TouchPoint GetTouchPoint(IInputElement relativeTo)
    {
        Point point = relativeTo is UIElement element ? canvas.TranslatePoint(_position, element) : _position;
        return new TouchPoint(this, point,
            new Rect(point.X - contactSize / 2, point.Y - contactSize / 2, contactSize, contactSize), _action);
    }

    public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo) => new() { GetTouchPoint(relativeTo) };

    internal void Down(double x, double y)
    {
        SetSample(TouchAction.Down, x, y);
        SetActiveSource(PresentationSource.FromVisual(canvas));
        Activate();
        ReportDown();
    }

    internal void Move(double x, double y)
    {
        SetSample(TouchAction.Move, x, y);
        ReportMove();
    }

    internal void Up(double x, double y)
    {
        SetSample(TouchAction.Up, x, y);
        ReportUp();
        Deactivate();
    }

    internal TouchEventArgs RaisePreview(RoutedEvent routedEvent, TouchAction action, double x, double y)
    {
        SetSample(action, x, y);
        var args = new TouchEventArgs(this, 0) { RoutedEvent = routedEvent };
        canvas.RaiseEvent(args);
        return args;
    }

    private void SetSample(TouchAction action, double x, double y)
    {
        _action = action;
        _position = new Point(x, y);
    }
}
