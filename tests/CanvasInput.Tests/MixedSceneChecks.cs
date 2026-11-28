using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentCanvas;
using FluentCanvas.Helpers;

internal static class MixedSceneChecks
{
    internal static void Run()
    {
        var window = new MainWindow();
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.icart");
        try
        {
            var pen = new Stroke(new StylusPointCollection {
                new StylusPoint(0,20,.3f), new StylusPoint(50,20,.5f), new StylusPoint(100,20,.7f)
            }, new DrawingAttributes { Width = 2, Height = 2 });
            window.Canvas.Strokes.Add(pen);
            var image = new Image
            {
                Width = 12,
                Height = 16,
                Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4)
            };
            Panel.SetZIndex(image, 7);
            InkCanvas.SetLeft(image, 4);
            InkCanvas.SetTop(image, 3);
            window.Canvas.Children.Add(image);
            window.History.CommitElementInsertHistory(image);
            var identity = SceneObject.ObjectId(image);
            var before = window.History.CaptureState();
            var rotation = Matrix.Identity;
            rotation.RotateAt(90, 50, 20);
            window.BeginBoardGesture();
            window.PreviewSelection([pen], [image], rotation);
            window.FinishBoardGesture();
            var after = window.History.CaptureState();
            var matrix = after.Page.ReadScene()[identity].Matrix;
            if (matrix != image.RenderTransform.Value || matrix.IsIdentity ||
                before.Page.ReadScene()[identity].Matrix != Matrix.Identity || before.Page.PageId != after.Page.PageId)
                throw new Exception("Mixed gesture changed the old page snapshot or lost the native placement.");

            window.BeginIndexedErase(5, false);
            window.AdvanceIndexedErase(new Point(30, 20));
            window.AdvanceIndexedErase(new Point(70, 20));
            window.FinishBoardGesture();
            if (window.Canvas.Strokes.Count != 2) throw new Exception("Eraser missed the rotated ink.");
            window.Undo();
            if (window.Canvas.Strokes.Single() != pen || image.RenderTransform.Value != matrix)
                throw new Exception("Erasure undo changed the mixed placement.");
            window.Undo();
            if (!image.RenderTransform.Value.IsIdentity || pen.StylusPoints[0].X != 0)
                throw new Exception("Mixed gesture did not undo in a single command.");
            window.Redo();
            using (var file = CanvasDocument.Capture(window.History.FreezeInk(), [image])) file.Save(path);
            var reopened = new InkCanvas();
            var history = new TimeMachine();
            CanvasDocument.LoadInto(path, reopened, history);
            var visual = (Image)reopened.Children[0];
            var state = history.CaptureState();
            if (SceneObject.ObjectId(visual) != identity || state.Page.PageId != before.Page.PageId ||
                visual.RenderTransform.Value != matrix || Panel.GetZIndex(visual) != 7 || InkCanvas.GetLeft(visual) != 4 ||
                state.Page.ReadScene()[identity].Matrix != matrix ||
                !reopened.Strokes[0].StylusPoints.SequenceEqual(pen.StylusPoints))
                throw new Exception("Reopening changed native page/object identity, layout or transformed ink.");
            CheckImmediateEdits(window, path);
            Console.WriteLine("PASS Mixed ink/image rotation, native point erasure, atomic undo, page/object identity and document reopen");
        }
        finally { window.Close(); File.Delete(path); }
    }

    private static void CheckImmediateEdits(MainWindow window, string path)
    {
        window.Show();
        var strokes = new StrokeCollection();
        for (int i = 0; i < 150; i++) strokes.Add(new Stroke(new StylusPointCollection(
            [new Point(i, 0), new Point(i, 12)])));
        window.Canvas.Strokes.Add(strokes);
        window.Canvas.UpdateLayout();
        Matrix delta = new(1, 0, 0, 1, 7, 0);
        void Move()
        {
            window.BeginBoardGesture();
            window.PreviewSelection(strokes.ToArray(), [], delta);
            window.FinishBoardGesture();
            if (!window.HasCommittedPreview) throw new Exception("Large gesture did not retain its visible preview.");
        }
        Move();
        using (var document = CanvasDocument.Capture(window.History.FreezeInk(), window.Canvas.Children.Cast<UIElement>()))
            document.Save(path);
        var copy = new InkCanvas();
        CanvasDocument.LoadInto(path, copy, new TimeMachine());
        if (copy.Strokes[^1].StylusPoints[0].X != 156)
            throw new Exception("Immediate save did not include the fully committed gesture.");
        window.Undo();
        if (window.HasCommittedPreview || strokes[0].StylusPoints[0].X != 0)
            throw new Exception("Undo left old preview on the canvas.");
        Move();
        Move();
        if (strokes[0].StylusPoints[0].X != 14) throw new Exception("Second gesture lost the previous placement.");
        window.BeginIndexedErase(3, true);
        window.AdvanceIndexedErase(new Point(15, 5));
        window.FinishBoardGesture();
        if (window.HasCommittedPreview || window.Canvas.Strokes.Contains(strokes[1]))
            throw new Exception("Immediate erasure retained committed overlay or hit stale coordinates.");
        window.Undo();
        Move();
        window.ResetCanvasInput();
        if (window.HasCommittedPreview) throw new Exception("Context change retained the old presenter handoff.");
        Move();
        var pen = new Stroke(new StylusPointCollection([new Point(30,30),new Point(50,60)]));
        window.Canvas.Strokes.Add(pen);
        if (window.HasCommittedPreview || !window.Canvas.Strokes.Contains(pen))
            throw new Exception("Fresh pen input was hidden by the preceding gesture.");
        Move();
        var ellipse = ParametricInk.Ellipse(new Point(4,4),new Point(40,32)).Create(pen.DrawingAttributes);
        window.Canvas.Strokes.Add(ellipse);
        if (window.HasCommittedPreview || !window.Canvas.Strokes.Contains(ellipse))
            throw new Exception("New geometry was hidden by the preceding gesture.");
        Move();
        window.AbortBoardGesture();
        if (window.HasCommittedPreview || window.Canvas.Strokes.Count != 0)
            throw new Exception("Clearing the page retained old geometry.");
    }
}
