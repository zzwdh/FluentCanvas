using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Threading;
using FluentCanvas.Helpers;

namespace FluentCanvas;

public partial class MainWindow
{
    private readonly System.Windows.Controls.Canvas ShapeHandles = new();
    private readonly Grid GridInkCanvasSelectionCover = new();
    private Stroke GenerateLineStroke(Point start, Point end) => CreatePlaneStroke(new[] {start,end});
    private void SetShapeDraft(StrokeCollection strokes) => inkCanvas.SetShapePreview(null, strokes);

    internal static void VerifyShapeHandles()
    {
        var window = new MainWindow();
        try {window.CheckShapeHandles();}
        finally {window.Close();}
    }

    private void CheckShapeHandles()
    {
        SetMultiTouch(false);
        Content = null;
        var layout = new Grid();
        layout.Children.Add(inkCanvas);
        layout.Children.Add(GridInkCanvasSelectionCover);
        layout.Children.Add(ShapeHandles);
        Content = layout;
        Show();
        UpdateLayout();

        var strokes = CreatePlaneShape(ShapeTool.Triangle,new Point(120,30),new Point(75,95),new Point(30,30));
        inkCanvas.Strokes.Add(strokes);
        inkCanvas.Select(strokes);
        RefreshShapeHandles();
        Require(ShapeHandles.Children.Count == 3, "Triangle controls are missing.");
        var vertex = (Thumb)ShapeHandles.Children[2];
        Drag(vertex, 12, 8, false);
        var triangle=strokes[0];
        var descriptor=ParametricInk.Get(triangle);
        Require((descriptor.C-new Point(87,103)).Length<0.1,"Dragging a vertex failed.");
        Undo();
        Require(ParametricInk.Get(triangle).C == new Point(75,95),"Undo failed to restore vertex.");
        Redo();
        inkCanvas.Select(strokes);
        RefreshShapeHandles();
        Drag((Thumb)ShapeHandles.Children[2], 30, 18, true);
        Require(ParametricInk.Get(triangle)==descriptor,"Canceled drag changed the committed shape.");

        var matrix = Matrix.Identity;
        matrix.RotateAt(30, 75,60);
        PreviewSelection([triangle], [], matrix);
        FinishBoardGesture();
        Require((ParametricInk.Get(triangle).C-matrix.Transform(descriptor.C)).Length < 1e-8,
            "Selection rotation changed the outline without updating its controls.");

        var number = DrawNumberLine(inkCanvas.DefaultDrawingAttributes,new(30,170),new(330,170),true);
        inkCanvas.Strokes.Add(number);
        inkCanvas.Select(number);
        RefreshShapeHandles();
        Require(ShapeHandles.Children.Count==2, "Number line components were not recognized as one group.");
        Drag((Thumb)ShapeHandles.Children[1], 35,0,false);
        Require(number.All(s=>Math.Abs(ParametricInk.Get(s).B.X-365)<0.1),"Label or tick detached from the resized number line.");
        Undo();
        Require(number.All(s=>Math.Abs(ParametricInk.Get(s).B.X-330)<0.1),"Undo did not restore the full number line.");
        inkCanvas.Select(new StrokeCollection());
        RefreshShapeHandles();
        Require(ShapeHandles.Children.Count==0,"Controls remained after deselection.");
        Console.WriteLine("PASS Actual teaching tools and WPF control handles drag, cancel, rotate, resize number lines and undo.");
    }

    private void Drag(Thumb thumb,double x,double y,bool cancel)
    {
        UpdateLayout();
        thumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
        thumb.RaiseEvent(new DragDeltaEventArgs(x,y){RoutedEvent=Thumb.DragDeltaEvent});
        thumb.RaiseEvent(new DragCompletedEventArgs(x,y,cancel){RoutedEvent=Thumb.DragCompletedEvent});
        Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    }

    private static void Require(bool ok,string error)
    {
        if (!ok) throw new InvalidOperationException(error);
    }
}
