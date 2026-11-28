using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        #region Floating Control

        object lastBorderMouseDownObject;

        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            lastBorderMouseDownObject = sender;
        }

        private void BorderStrokeSelectionCloneToBoardOrNewPage_Click(object sender, RoutedEventArgs e)
        {
            if (currentMode != 0 && !Pages.CanInsert) return;
            if (currentMode == 0)
            {
                StrokeCollection strokes = inkCanvas.GetSelectedStrokes();
                List<UIElement> elements = FluentCanvasElementsHelper.GetSelectedElementsCloned(inkCanvas);
                inkCanvas.Select(new StrokeCollection());
                strokes = strokes.Clone();
                ImageBlackboard_Click(null, null);
                inkCanvas.Strokes.Add(strokes);
                FluentCanvasElementsHelper.AddElements(inkCanvas, elements, timeMachine);
            }
            else
            {
                StrokeCollection strokes = inkCanvas.GetSelectedStrokes();
                List<UIElement> elements = FluentCanvasElementsHelper.GetSelectedElementsCloned(inkCanvas);
                inkCanvas.Select(new StrokeCollection());
                strokes = strokes.Clone();
                AddWhiteboard();
                inkCanvas.Strokes.Add(strokes);
                FluentCanvasElementsHelper.AddElements(inkCanvas, elements, timeMachine);
            }
        }

        private void GridPenWidthDecrease_Click(object sender, RoutedEventArgs e)
        {
            ChangeSelectedStrokeThickness(0.8);
        }

        private void GridPenWidthIncrease_Click(object sender, RoutedEventArgs e)
        {
            ChangeSelectedStrokeThickness(1.25);
        }

        private void GridPenWidthRestore_Click(object sender, RoutedEventArgs e)
        {
            foreach (Stroke stroke in inkCanvas.GetSelectedStrokes())
            {
                stroke.DrawingAttributes.Width = inkCanvas.DefaultDrawingAttributes.Width;
                stroke.DrawingAttributes.Height = inkCanvas.DefaultDrawingAttributes.Height;
            }
        }

        private void BorderStrokeSelectionDelete_Click(object sender, RoutedEventArgs e)
        {
            SymbolIconDelete_MouseUp(sender, e);
        }

        private async void BtnStrokeSelectionSaveToImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var bitmap = CanvasSelectionImage.Render(inkCanvas);
                if (bitmap == null) return;

                string path = await WinUiPickers.PickSaveFileAsync(new WindowInteropHelper(this).Handle,
                    "FluentCanvas.ExportSelection", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff"), UiText.Get("Actions.PngImage"), ".png");
                if (path == null) return;

                await screenshotWrites.Enqueue(() =>
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    AtomicFile.Write(path, encoder.Save);
                });
            }
            catch (Exception ex)
            {
                ShowNotificationAsync(UiText.Get("Actions.SelectionExportFailed"));
                LogHelper.WriteLogToFile("导出选区失败 | " + ex, LogHelper.LogType.Error);
            }
        }

        private void ChangeSelectedStrokeThickness(double multipler)
        {
            foreach (Stroke stroke in inkCanvas.GetSelectedStrokes())
            {
                var newWidth = stroke.DrawingAttributes.Width * multipler;
                var newHeight = stroke.DrawingAttributes.Height * multipler;
                if (newWidth >= DrawingAttributes.MinWidth && newWidth <= DrawingAttributes.MaxWidth
                    && newHeight >= DrawingAttributes.MinHeight && newHeight <= DrawingAttributes.MaxHeight)
                {
                    stroke.DrawingAttributes.Width = newWidth;
                    stroke.DrawingAttributes.Height = newHeight;
                }
            }
            CommitPendingStrokeDrawingAttributes();
        }

        private void MatrixTransform(int type)
        {
            Matrix m = new Matrix();
            Rect bounds = inkCanvas.GetSelectionBounds();
            Point center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

            switch (type)
            {
                case 1: // Flip Horizontal
                    m.ScaleAt(-1, 1, center.X, center.Y);
                    break;
                case 2: // Flip Vertical
                    m.ScaleAt(1, -1, center.X, center.Y);
                    break;
                default: // Rotate
                    m.RotateAt(type, center.X, center.Y);
                    break;
            }

            var strokes = inkCanvas.GetSelectedStrokes();
            var elements = inkCanvas.GetSelectedElements();
            if (strokes.Count == 0 && elements.Count == 0) return;
            BeginContentGesture(strokes, elements, true);
            contentGesture.Append(m, 1, 1);
            FinishContentGesture();
        }

        private void BtnFlipHorizontal_Click(object sender, RoutedEventArgs e)
        {
            MatrixTransform(1);
        }

        private void BtnFlipVertical_Click(object sender, RoutedEventArgs e)
        {
            MatrixTransform(2);
        }

        private void BtnAnticlockwiseRotate15_Click(object sender, RoutedEventArgs e)
        {
            MatrixTransform(-15);
        }

        private void BtnAnticlockwiseRotate45_Click(object sender, RoutedEventArgs e)
        {
            MatrixTransform(-45);
        }

        private void BtnAnticlockwiseRotate90_Click(object sender, RoutedEventArgs e)
        {
            MatrixTransform(-90);
        }

        private void BtnClockwiseRotate15_Click(object sender, RoutedEventArgs e)
        {
            MatrixTransform(15);
        }

        private void BtnClockwiseRotate45_Click(object sender, RoutedEventArgs e)
        {
            MatrixTransform(45);
        }

        private void BtnClockwiseRotate90_Click(object sender, RoutedEventArgs e)
        {
            MatrixTransform(90);
        }

        #endregion

        bool isGridInkCanvasSelectionCoverMouseDown = false;
        private Point lastMousePoint;

        private void GridInkCanvasSelectionCover_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch) return;
            gestureCancelled = false;
            lastMousePoint = e.GetPosition(inkCanvas);
            isGridInkCanvasSelectionCoverMouseDown = true;
            if (ToggleButtonStrokeSelectionClone.IsChecked == true)
            {
                StrokeCollection strokes = inkCanvas.GetSelectedStrokes();
                List<UIElement> elementsList = FluentCanvasElementsHelper.GetSelectedElements(inkCanvas);
                isProgramChangeStrokeSelection = true;
                ElementsSelectionClone = FluentCanvasElementsHelper.CloneSelectedElements(inkCanvas, ref ElementsInitialHistory);
                inkCanvas.Select(new StrokeCollection());
                StrokesSelectionClone = strokes.Clone();
                inkCanvas.Strokes.Add(StrokesSelectionClone);
                inkCanvas.Select(strokes, elementsList);
                isProgramChangeStrokeSelection = false;
            }
            else if (lastMousePoint.X < inkCanvas.GetSelectionBounds().Left ||
            lastMousePoint.Y < inkCanvas.GetSelectionBounds().Top ||
            lastMousePoint.X > inkCanvas.GetSelectionBounds().Right ||
            lastMousePoint.Y > inkCanvas.GetSelectionBounds().Bottom)
            {
                isGridInkCanvasSelectionCoverMouseDown = false;
                inkCanvas.Select(new StrokeCollection());
                StrokesSelectionClone = new StrokeCollection();
                ElementsSelectionClone = new List<UIElement>();
            }
            if (isGridInkCanvasSelectionCoverMouseDown && e.StylusDevice == null && e.ChangedButton == MouseButton.Left)
                GridInkCanvasSelectionCover.CaptureMouse();
        }

        private void GridInkCanvasSelectionCover_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isGridInkCanvasSelectionCoverMouseDown || e.LeftButton != MouseButtonState.Pressed ||
                e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch) return;
            Point mousePoint = e.GetPosition(inkCanvas);
            Vector trans = new Vector(mousePoint.X - lastMousePoint.X, mousePoint.Y - lastMousePoint.Y);
            lastMousePoint = mousePoint;
            Matrix m = new Matrix();
            // add Translate
            m.Translate(trans.X, trans.Y);
            PreviewSelectionGesture(m, 1, 1);
            updateBorderStrokeSelectionControlLocation();
        }

        private void GridInkCanvasSelectionCover_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch) return;
            ToCommitStrokeManipulationHistoryAfterMouseUp();
            if (Mouse.Captured == GridInkCanvasSelectionCover) GridInkCanvasSelectionCover.ReleaseMouseCapture();
            isGridInkCanvasSelectionCoverMouseDown = false;
            if (FluentCanvasElementsHelper.IsNotCanvasElementSelected(inkCanvas))
            {
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
                StrokesSelectionClone = new StrokeCollection();
                ElementsSelectionClone = new List<UIElement>();
            }
            else
            {
                if (currentMode == 0)
                {
                    TextSelectionCloneToNewBoard.Text = UiText.Get("Actions.CloneToBoard");
                }
                else
                {
                    TextSelectionCloneToNewBoard.Text = UiText.Get("Actions.CloneToNewPage");
                }
                GridInkCanvasSelectionCover.Visibility = Visibility.Visible;
                StrokesSelectionClone = new StrokeCollection();
                ElementsSelectionClone = new List<UIElement>();
            }
        }

        private void GridInkCanvasSelectionCover_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double scale = e.Delta > 0 ? 1.1 : 0.9;
            Point center = FluentCanvasElementsHelper.GetAllElementsBoundsCenterPoint(inkCanvas);
            Matrix m = new Matrix();
            m.ScaleAt(scale, scale, center.X, center.Y);

            gestureCancelled = false;
            PreviewSelectionGesture(m, scale, scale);
            FinishContentGesture();
            updateBorderStrokeSelectionControlLocation();
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = ShapeTool.Pen;
            inkCanvas.IsManipulationEnabled = false;
            if (inkCanvas.EditingMode == InkCanvasEditingMode.Select)
            {
                if (inkCanvas.GetSelectedStrokes().Count == inkCanvas.Strokes.Count
                    && inkCanvas.GetSelectedElements().Count == inkCanvas.Children.Count)
                {
                    inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                    inkCanvas.EditingMode = InkCanvasEditingMode.Select;
                }
                else
                {
                    StrokeCollection selectedStrokes = new StrokeCollection();
                    foreach (Stroke stroke in inkCanvas.Strokes)
                    {
                        if (stroke.GetBounds().Width > 0 && stroke.GetBounds().Height > 0)
                        {
                            selectedStrokes.Add(stroke);
                        }
                    }
                    List<UIElement> selectedElements = FluentCanvasElementsHelper.GetAllElements(inkCanvas);
                    inkCanvas.Select(selectedStrokes, selectedElements);
                }
            }
            else
            {
                inkCanvas.EditingMode = InkCanvasEditingMode.Select;
            }
        }
        bool isProgramChangeStrokeSelection = false;

        private void inkCanvas_SelectionChanged(object sender, EventArgs e)
        {
            if (isProgramChangeStrokeSelection) return;
            CancelContentGesture();
            if (FluentCanvasElementsHelper.IsNotCanvasElementSelected(inkCanvas))
            {
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (currentMode == 0)
                {
                    TextSelectionCloneToNewBoard.Text = UiText.Get("Actions.CloneToBoard");
                }
                else
                {
                    TextSelectionCloneToNewBoard.Text = UiText.Get("Actions.CloneToNewPage");
                }
                GridInkCanvasSelectionCover.Visibility = Visibility.Visible;
                ToggleButtonStrokeSelectionClone.IsChecked = false;
                updateBorderStrokeSelectionControlLocation();
            }
            RefreshShapeHandles();
        }
        double BorderStrokeSelectionControlWidth = 695;
        double BorderStrokeSelectionControlHeight = 104;

        private void updateBorderStrokeSelectionControlLocation()
        {
            Rect selectionBounds = CurrentSelectionBounds();
            double borderLeft = (selectionBounds.Left + selectionBounds.Right - BorderStrokeSelectionControlWidth) / 2;
            double borderTop = selectionBounds.Bottom + 15;

            // ensure the border is inside the window
            borderLeft = Math.Max(0, borderLeft);
            borderTop = Math.Max(0, borderTop);
            borderLeft = Math.Min(Width - BorderStrokeSelectionControlWidth, borderLeft);
            borderTop = Math.Min(Height - BorderStrokeSelectionControlHeight, borderTop);

            double borderBottom = borderTop + BorderStrokeSelectionControlHeight;
            double borderRight = borderLeft + BorderStrokeSelectionControlWidth;

            double viewboxTop = ViewboxFloatingBar.Margin.Top;
            double viewboxLeft = ViewboxFloatingBar.Margin.Left;
            double viewboxBottom = viewboxTop + ViewboxFloatingBar.ActualHeight;
            double viewboxRight = viewboxLeft + ViewboxFloatingBar.ActualWidth;

            if (currentMode == 0)
            {
                bool isHorizontalOverlap = (borderLeft < viewboxRight && borderRight > viewboxLeft);
                bool isVerticalOverlap = (borderTop < viewboxBottom && borderBottom > viewboxTop);
                if (isHorizontalOverlap && isVerticalOverlap)
                {
                    double belowViewboxMargin = viewboxBottom + 5;
                    double maxBottomPositionMargin = Height - BorderStrokeSelectionControlHeight;
                    borderTop = belowViewboxMargin > maxBottomPositionMargin
                        ? viewboxTop - BorderStrokeSelectionControlHeight - 5
                        : belowViewboxMargin;
                }
            }
            else
            {
                borderTop = Math.Min(Height - BorderStrokeSelectionControlHeight - 60, borderTop);
            }
            if (!double.IsNaN(borderLeft) && !double.IsNaN(borderTop))
            {
                BorderStrokeSelectionControl.Margin = new Thickness(borderLeft, borderTop, 0, 0);
            }
            else
            {
                BorderStrokeSelectionControl.Margin = new Thickness(0, 0, 0, 0);
            }
        }

        private void GridInkCanvasSelectionCover_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
        {
            gestureCancelled = false;
            e.ManipulationContainer = inkCanvas;
            e.Mode = ManipulationModes.All;
        }

        private void GridInkCanvasSelectionCover_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            CommitPendingStrokeDrawingAttributes();
            updateBorderStrokeSelectionControlLocation();
        }

        StrokeCollection StrokesSelectionClone = new StrokeCollection();
        List<UIElement> ElementsSelectionClone = new List<UIElement>();

        private void GridInkCanvasSelectionCover_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            if (dec.Count == 0 || gestureCancelled) return;
            var delta = e.DeltaManipulation;
            var center = GetMatrixTransformCenterPoint(e.ManipulationOrigin, e.Source as FrameworkElement);
            var matrix = Matrix.Identity;
            matrix.ScaleAt(delta.Scale.X, delta.Scale.Y, center.X, center.Y);
            if (StrokesSelectionClone.Count == 0 && Settings.Gesture.IsEnableTwoFingerRotationOnSelection)
                matrix.RotateAt(delta.Rotation, center.X, center.Y);
            matrix.Translate(delta.Translation.X, delta.Translation.Y);
            PreviewSelectionGesture(matrix, delta.Scale.X, delta.Scale.Y);
            updateBorderStrokeSelectionControlLocation();
        }

        private void PreviewSelectionGesture(Matrix matrix, double scaleX, double scaleY)
        {
            if (gestureCancelled || matrix.IsIdentity) return;
            if (contentGesture == null)
            {
                bool cloning = StrokesSelectionClone.Count > 0 || ElementsSelectionClone.Count > 0;
                var strokes = cloning ? StrokesSelectionClone : inkCanvas.GetSelectedStrokes();
                var elements = cloning ? ElementsSelectionClone : FluentCanvasElementsHelper.GetSelectedElements(inkCanvas);
                BeginContentGesture(strokes, elements, !cloning);
            }
            contentGesture.Append(matrix, scaleX, scaleY);
        }

        private Rect CurrentSelectionBounds()
        {
            if (contentGesture is { MovesSelection: true } gesture)
                return Rect.Transform(gesture.SelectionBounds, gesture.Matrix);
            return inkCanvas.GetSelectionBounds();
        }
        Point lastTouchPointOnGridInkCanvasCover = new Point(0, 0);
        private void GridInkCanvasSelectionCover_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            dec.Add(e.TouchDevice.Id);
            //设备1个的时候，记录中心点
            if (dec.Count == 1)
            {
                TouchPoint touchPoint = e.GetTouchPoint(null);
                lastTouchPointOnGridInkCanvasCover = touchPoint.Position;

                if (ToggleButtonStrokeSelectionClone.IsChecked == true)
                {
                    StrokeCollection strokes = inkCanvas.GetSelectedStrokes();
                    List<UIElement> elementsList = FluentCanvasElementsHelper.GetSelectedElements(inkCanvas);
                    isProgramChangeStrokeSelection = true;
                    ElementsSelectionClone = FluentCanvasElementsHelper.CloneSelectedElements(inkCanvas, ref ElementsInitialHistory);
                    inkCanvas.Select(new StrokeCollection());
                    StrokesSelectionClone = strokes.Clone();
                    inkCanvas.Strokes.Add(StrokesSelectionClone);
                    inkCanvas.Select(strokes, elementsList);
                    isProgramChangeStrokeSelection = false;
                }
            }
        }

        private void GridInkCanvasSelectionCover_PreviewTouchUp(object sender, TouchEventArgs e)
        {
            dec.Remove(e.TouchDevice.Id);
            if (dec.Count >= 1) return;
            isProgramChangeStrokeSelection = false;
            if (lastTouchPointOnGridInkCanvasCover == e.GetTouchPoint(null).Position)
            {
                if (lastTouchPointOnGridInkCanvasCover.X < inkCanvas.GetSelectionBounds().Left ||
                    lastTouchPointOnGridInkCanvasCover.Y < inkCanvas.GetSelectionBounds().Top ||
                    lastTouchPointOnGridInkCanvasCover.X > inkCanvas.GetSelectionBounds().Right ||
                    lastTouchPointOnGridInkCanvasCover.Y > inkCanvas.GetSelectionBounds().Bottom)
                {
                    inkCanvas.Select(new StrokeCollection());
                    StrokesSelectionClone = new StrokeCollection();
                    ElementsSelectionClone = new List<UIElement>();
                }
            }
            else if (FluentCanvasElementsHelper.IsNotCanvasElementSelected(inkCanvas))
            {
                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
                StrokesSelectionClone = new StrokeCollection();
                ElementsSelectionClone = new List<UIElement>();
            }
            else
            {
                if (currentMode == 0)
                {
                    TextSelectionCloneToNewBoard.Text = UiText.Get("Actions.CloneToBoard");
                }
                else
                {
                    TextSelectionCloneToNewBoard.Text = UiText.Get("Actions.CloneToNewPage");
                }
                GridInkCanvasSelectionCover.Visibility = Visibility.Visible;
                StrokesSelectionClone = new StrokeCollection();
                ElementsSelectionClone = new List<UIElement>();
            }
        }
    }
}
