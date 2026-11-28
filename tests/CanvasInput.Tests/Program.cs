using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FluentCanvas;
using FluentCanvas.Helpers;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            VerifyFrameBatchesAndCompletion();
            VerifyCancellationAndRollback();
            VerifyFailedCapture();
            VerifyManagedContactRouting();
            VerifyTouchEraserLifecycle();
            ShapeCandidateChecks.RunAsync().GetAwaiter().GetResult();
            BoardHistoryChecks.Run();
            IndexedEraseChecks.Run();
            MainWindow.VerifyShapeHandles();
            PageNavigationChecks.Run();
            MainWindow.VerifyBoardPages();
            MainWindow.VerifyPresentationBackup();
            VerifyPanelMotion();
            Console.WriteLine("14 canvas regression groups passed.");
            if (args.Contains("--measure-history")) BoardHistoryChecks.Measure();
            if (args.Contains("--measure-erase")) IndexedEraseChecks.Measure();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void VerifyFrameBatchesAndCompletion()
    {
        var canvas = new InkCanvas();
        var preview = new MultiTouchInkPreview(canvas);
        var committed = 0;
        canvas.Strokes.StrokesChanged += (_, args) => committed += args.Added.Count;

        preview.Begin(1, Points(0, 4));
        var firstVisual = DrawingFor(canvas.Children[0]);
        preview.Begin(2, Points(10, 4));
        var secondVisual = DrawingFor(canvas.Children[1]);
        preview.Append(1, Points(4, 3));
        preview.Append(2, Points(14, 3));
        preview.Append(1, Points(7, 3));
        Check(firstVisual.Drawing == null && secondVisual.Drawing == null && committed == 0,
            "Incoming batches should queue drawing without committing strokes.");
        DispatchFrame(preview);
        Check(firstVisual.Drawing?.Children.Count > 0 && secondVisual.Drawing?.Children.Count > 0,
            "Both active contacts should render on the queued frame.");
        Check(MultiTouchInkPreview.IsTransient(canvas.Children[0]) && !canvas.Children[0].IsHitTestVisible,
            "A preview should be transient and let input through.");

        var first = preview.Complete(1, Points(10, 1));
        Check(first.StylusPoints.Count == 11 && committed == 1, "The first contact should commit exactly once.");
        for (int i = 0; i < first.StylusPoints.Count; i++)
        {
            var point = first.StylusPoints[i];
            Check(point.X == i && point.Y == i * 2 && point.PressureFactor == Pressure(i),
                "Every point should retain its order, coordinates and pressure.");
        }

        Check(canvas.Children.Count == 1 && preview.Contains(2), "Releasing one contact should leave the other active.");
        preview.Append(2, Points(17, 3));
        var second = preview.Complete(2, Points(20, 1));
        Check(second.StylusPoints.Count == 11 && committed == 2, "Completion should keep points queued since the previous frame.");
        Check(preview.Complete(2, Points(21, 1)) == null && canvas.Children.Count == 0,
            "A duplicate release should have no effect.");
        Console.WriteLine("PASS Frame batching and staggered commits preserve every sample and pressure value");
    }

    private static void VerifyCancellationAndRollback()
    {
        var canvas = new InkCanvas();
        var preview = new MultiTouchInkPreview(canvas);
        preview.Begin(3, Points(30, 4));
        preview.Begin(4, Points(40, 4));
        preview.Cancel(3);
        Check(preview.Complete(3, Points(34, 1)) == null && preview.Contains(4) && canvas.Strokes.Count == 0,
            "Canceling one preview should keep the other active without committing ink.");
        preview.Begin(3, Points(50, 1));
        var tap = preview.Complete(3, new StylusPointCollection());
        Check(tap.StylusPoints.Count == 1 && tap.StylusPoints[0].X == 50, "A reused contact ID should accept a tap.");
        preview.CancelAll();
        DispatchFrame(preview);
        Check(!preview.Contains(4) && canvas.Children.Count == 0 && canvas.Strokes.Count == 1,
            "Canceling all contacts should remove every preview even with a queued frame.");

        preview.Begin(5, Points(0, 4));
        var detachedChild = canvas.Children[0];
        var detachedDrawing = DrawingFor(detachedChild);
        DispatchFrame(preview);
        canvas.Children.Clear();
        Check(!preview.Contains(5) && preview.Complete(5, Points(4, 1)) == null,
            "Clearing canvas children should invalidate the in-flight preview immediately.");
        Check(detachedDrawing.Drawing == null || detachedDrawing.Drawing.Children.Count == 0,
            "An expired preview should clear its drawing.");
        canvas.Children.Add(detachedChild);
        canvas.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Check(canvas.Children.Count == 0 && canvas.Strokes.Count == 1,
            "An expired preview should be removed even if a rollback reattaches it.");
        Console.WriteLine("PASS Contact cancellation, ID reuse, canvas clearing and document rollback");
    }

    private static void VerifyFailedCapture()
    {
        var window = new MainWindow();
        try
        {
            var canvas = window.Canvas;
            var device = new ManagedTouchDevice(canvas, 10);
            var down = device.RaisePreview(UIElement.PreviewTouchDownEvent, TouchAction.Down, 24, 25);
            Check(down.Handled && device.Captured == null && canvas.Children.Count == 0 && canvas.Strokes.Count == 0,
                "A device without a presentation source cannot capture and should leave no preview.");
            Check(canvas.EditingMode == InkCanvasEditingMode.Ink, "A failed first capture should restore native ink mode.");
            device.RaisePreview(UIElement.PreviewTouchUpEvent, TouchAction.Up, 30, 35);
            Check(canvas.Strokes.Count == 0, "The late release of a failed capture should never commit.");
        }
        finally
        {
            window.Close();
        }
        Console.WriteLine("PASS Failed WPF capture drops the preview and restores native ink mode");
    }

    private static void VerifyManagedContactRouting()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var canvas = window.Canvas;
            var first = new ManagedTouchDevice(canvas, 20);
            var second = new ManagedTouchDevice(canvas, 21);
            first.Down(35, 40);
            second.Down(35, 80);
            Check(first.Captured == canvas && second.Captured == canvas && canvas.Children.Count == 2 && canvas.Strokes.Count == 0,
                "Both managed touch events should capture and get separate previews.");
            var rejected = new ManagedTouchDevice(canvas, 24);
            void ReleaseRejectedCapture(object sender, TouchEventArgs args)
            {
                if (ReferenceEquals(args.TouchDevice, rejected)) rejected.Capture(null);
            }
            canvas.GotTouchCapture += ReleaseRejectedCapture;
            rejected.RaisePreview(UIElement.PreviewTouchDownEvent, TouchAction.Down, 38, 88);
            canvas.GotTouchCapture -= ReleaseRejectedCapture;
            Check(rejected.Captured == null && canvas.Children.Count == 2 && canvas.EditingMode == InkCanvasEditingMode.None,
                "Losing a new capture immediately should leave existing captures and their editing mode in place.");

            first.Move(45, 53);
            second.Move(50, 72);
            first.Move(55, 56);
            first.Up(65, 43);
            Check(canvas.Strokes.Count == 1 && canvas.Children.Count == 1 && canvas.EditingMode == InkCanvasEditingMode.None,
                "The first lift should commit its own current stroke and leave the other active.");
            second.Move(61, 77);
            second.Up(70, 85);
            Check(canvas.Strokes.Count == 2 && window.CollectedCount == 2 && canvas.Children.Count == 0 && canvas.EditingMode == InkCanvasEditingMode.Ink,
                "The second lift should finish the last contact and restore native ink mode.");
            var stroke = canvas.Strokes[0].StylusPoints;
            Check(stroke.Count == 4 && stroke[0].X == 35 && stroke[1].Y == 53 && stroke[2].Y == 56 && stroke[3].X == 65,
                "Routed moves should appear in the completed stroke in order.");

            var captured = new ManagedTouchDevice(canvas, 22);
            captured.Down(80, 40);
            var stale = new ManagedTouchDevice(canvas, 22);
            stale.RaisePreview(UIElement.PreviewTouchUpEvent, TouchAction.Up, 120, 60);
            Check(canvas.Children.Count == 1 && canvas.Strokes.Count == 2 && captured.Captured == canvas,
                "A late event from a different device with a reused ID must not finish the current contact.");
            captured.Capture(null);
            Check(canvas.Children.Count == 0 && canvas.EditingMode == InkCanvasEditingMode.Ink,
                "Losing capture should discard the in-flight stroke and restore native ink.");
            captured.Up(100, 45);

            var reset = new ManagedTouchDevice(canvas, 23);
            reset.Down(110, 53);
            window.ResetCanvasInput();
            Check(reset.Captured == null && canvas.Children.Count == 0 && canvas.EditingMode == InkCanvasEditingMode.Ink,
                "A page or document reset should discard touch previews and release their captures.");
            reset.Up(140, 65);
            Check(canvas.Strokes.Count == 2 && window.CollectedCount == 2, "Canceled contacts should never generate late strokes.");

            canvas.EditingMode = InkCanvasEditingMode.Select;
            window.SetMultiTouch(false);
            window.SetMultiTouch(true);
            Check(canvas.EditingMode == InkCanvasEditingMode.Select, "The multitouch toggle should preserve the current tool.");
        }
        finally
        {
            window.Close();
        }
        Console.WriteLine("PASS Managed WPF touch routing, staggered lifts, capture loss and context reset");
    }

    private static void VerifyTouchEraserLifecycle()
    {
        var window = new MainWindow();
        try
        {
            var canvas = window.Canvas;
            var eraser = new ManagedTouchDevice(canvas, 31, 220);
            eraser.RaisePreview(UIElement.PreviewTouchDownEvent, TouchAction.Down, 40, 40);
            Check(canvas.EditingMode == InkCanvasEditingMode.EraseByPoint && canvas.EraserShape.Width == 220,
                "A wide contact should enter point erasure at its configured diameter.");
            var stale = new ManagedTouchDevice(canvas, 31);
            stale.RaisePreview(UIElement.PreviewTouchUpEvent, TouchAction.Up, 42, 44);
            canvas.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Check(canvas.EditingMode == InkCanvasEditingMode.EraseByPoint,
                "An unrelated device reusing the eraser ID must leave the active eraser alone.");
            eraser.RaisePreview(UIElement.PreviewTouchUpEvent, TouchAction.Up, 42, 44);
            window.SetMultiTouch(false);
            Check(canvas.EditingMode == InkCanvasEditingMode.Ink,
                "Turning multi-touch off during the deferred eraser release should restore native ink immediately.");
            canvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            canvas.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Check(canvas.EditingMode == InkCanvasEditingMode.EraseByStroke,
                "A disposed session must leave the user's next tool selection intact.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS Touch eraser ownership, contact ID reuse and deferred mode restoration");
    }

    private static void DispatchFrame(MultiTouchInkPreview preview)
    {
        // Exercise the subscribed handler directly so frame timing never depends on window composition.
        var handler = typeof(MultiTouchInkPreview).GetMethod("RenderPending", BindingFlags.Instance | BindingFlags.NonPublic);
        handler!.Invoke(preview, [null, EventArgs.Empty]);
    }

    private static void VerifyPanelMotion()
    {
        var originalTransform = new ScaleTransform(1.15, 0.9);
        var panel = new Border
        {
            Width = 40,
            Height = 40,
            Background = Brushes.Black,
            Visibility = Visibility.Collapsed,
            Opacity = 0.65,
            RenderTransform = originalTransform
        };
        var window = new Window
        {
            Width = 180,
            Height = 120,
            ShowInTaskbar = false,
            ShowActivated = false,
            Opacity = 0,
            Content = panel
        };

        bool Restored() => ReferenceEquals(panel.RenderTransform, originalTransform) && panel.Opacity == 0.65;
        window.Show();
        try
        {
            DispatchUntil(() => panel.IsLoaded, "The transition host should load its panel.");
            bool animated = AnimationsHelper.CanAnimate;
            AnimationsHelper.ShowWithSlideFromBottomAndFade(panel);
            Check(AnimationsHelper.IsShown(panel) && panel.Visibility == Visibility.Visible,
                "An opening panel should immediately become available.");
            if (animated)
                Check(!ReferenceEquals(panel.RenderTransform, originalTransform), "An opening panel should start a slide.");
            else
                Check(Restored(), "Reduced motion should open the panel immediately without temporary effects.");
            DispatchUntil(Restored, "Opening should restore the panel's opacity and transform after motion.");

            AnimationsHelper.HideWithSlideAndFade(panel);
            Check(!AnimationsHelper.IsShown(panel) && panel.Visibility == (animated ? Visibility.Visible : Visibility.Collapsed),
                "Closing should change the logical state immediately and only retain the visual while animating.");
            if (animated)
            {
                DispatchUntil(() => !panel.IsHitTestVisible || panel.Visibility == Visibility.Collapsed,
                    "The closing panel should stop handling input.");
                Check(panel.IsVisible && !panel.IsHitTestVisible,
                    "The closing panel should let input through while it is still visible.");
            }
            AnimationsHelper.ShowWithSlideFromBottomAndFade(panel);
            DispatchUntil(Restored, "Reversing an in-flight close should restore the panel's visual properties.");
            Check(AnimationsHelper.IsShown(panel) && panel.IsVisible && panel.IsHitTestVisible,
                "A reversed close must leave the panel shown and interactive.");

            AnimationsHelper.HideWithSlideAndFade(panel);
            DispatchUntil(() => panel.Visibility == Visibility.Collapsed,
                "A completed close should collapse the panel.");
            Check(Restored() && (bool)panel.GetAnimationBaseValue(UIElement.IsHitTestVisibleProperty),
                "A completed close should restore the panel's base visual and input properties.");

            panel.IsHitTestVisible = false;
            AnimationsHelper.ShowWithSlideFromBottomAndFade(panel);
            AnimationsHelper.HideImmediately(panel);
            Check(!AnimationsHelper.IsShown(panel) && panel.Visibility == Visibility.Collapsed && Restored() && !panel.IsHitTestVisible,
                "Immediate dismissal should cancel the effects and preserve the original input setting.");
        }
        finally
        {
            window.Close();
        }
        Console.WriteLine("PASS Panel motion reverses, completes and preserves base properties with the current Windows preferences");
    }

    private static void DispatchUntil(Func<bool> completed, string description)
    {
        if (completed()) return;

        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => frame.Continue = !completed() && elapsed.Elapsed < TimeSpan.FromSeconds(2);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Check(completed(), description);
    }

    private static DrawingVisual DrawingFor(UIElement element) => (DrawingVisual)VisualTreeHelper.GetChild(element, 0);

    private static StylusPointCollection Points(int start, int count)
    {
        var points = new StylusPointCollection(count);
        for (int i = start; i < start + count; i++)
            points.Add(new StylusPoint(i, i * 2, Pressure(i)));
        return points;
    }

    private static float Pressure(int index) => (index % 5 + 1) * 0.125f;

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
