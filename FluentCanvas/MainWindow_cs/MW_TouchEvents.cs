using FluentCanvas.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        private MultiTouchInkSession touchInk;
        private bool isInMultiTouchMode => touchInk != null;

        private void BorderMultiTouchMode_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (touchInk != null)
            {
                var ending = touchInk;
                touchInk = null;
                ending.Dispose();
                return;
            }
            touchInk = new MultiTouchInkSession(inkCanvas, this, dec,
                () => drawingShapeMode == ShapeTool.Pen && !forceEraser && !isSingleFingerDragMode,
                () => forceEraser);
        }

        private void HidePanelsForInking()
        {
            if (isHidingSubPanelsWhenInking) return;
            isHidingSubPanelsWhenInking = true;
            HideSubPanels();
        }

        private TouchEraser? GetTouchEraser(TouchEventArgs e)
        {
            var advanced = Settings.Advanced;
            double width = GetTouchBoundWidth(e);
            if ((advanced.IsSpecialScreen && advanced.TouchMultiplier == 0) || !(width > BoundsWidth)) return null;

            bool nib = Settings.Startup.IsEnableNibMode;
            double threshold = nib ? advanced.NibModeBoundsWidthThresholdValue : advanced.FingerModeBoundsWidthThresholdValue;
            if (!(width > BoundsWidth * threshold)) return new TouchEraser(InkCanvasEditingMode.EraseByStroke, 5);
            width *= nib ? advanced.NibModeBoundsWidthEraserSize : advanced.FingerModeBoundsWidthEraserSize;
            if (advanced.IsSpecialScreen) width *= advanced.TouchMultiplier;
            return new TouchEraser(InkCanvasEditingMode.EraseByPoint, width);
        }

        private void CancelPendingCanvasInput()
        {
            CancelShapeDraft();
            touchInk?.Cancel();
            dec.Clear();
            InvalidateInkRecognition();
            inkCanvas.Opacity = 1;
        }

        int lastTouchDownTime = 0, lastTouchUpTime = 0;

        Point iniP = new Point(0, 0);
        bool isLastTouchEraser = false;
        private bool forcePointEraser = true;

        private void Main_Grid_TouchDown(object sender, TouchEventArgs e)
        {
            if (isInMultiTouchMode) return;

            HidePanelsForInking();

            if (NeedUpdateIniP())
            {
                iniP = e.GetTouchPoint(inkCanvas).Position;
            }
            if (drawingShapeMode == ShapeTool.Cuboid && isFirstTouchCuboid == false)
            {
                MouseTouchMove(iniP);
            }
            inkCanvas.Opacity = 1;
            if (GetTouchEraser(e) is { } eraser)
            {
                isLastTouchEraser = true;
                if (drawingShapeMode == ShapeTool.Pen && forceEraser) return;
                if (eraser.Mode == InkCanvasEditingMode.EraseByStroke && BtnPPTSlideShowEnd.Visibility == Visibility.Visible &&
                    inkCanvas.Strokes.Count == 0 && Settings.PowerPointSettings.IsEnableFingerGestureSlideShowControl)
                {
                    isLastTouchEraser = false;
                    inkCanvas.EditingMode = InkCanvasEditingMode.GestureOnly;
                    inkCanvas.Opacity = 0.1;
                }
                else eraser.Apply(inkCanvas);
            }
            else
            {
                isLastTouchEraser = false;
                inkCanvas.EraserShape = forcePointEraser ? new EllipseStylusShape(50, 50) : new EllipseStylusShape(5, 5);
                if (forceEraser) return;
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            }
        }

        public double GetTouchBoundWidth(TouchEventArgs e)
        {
            var args = e.GetTouchPoint(null).Bounds;
            if (!Settings.Advanced.IsQuadIR) return args.Width;
            else return Math.Sqrt(args.Width * args.Height); //四边红外
        }

        //记录触摸设备ID
        private readonly HashSet<int> dec = new();
        InkCanvasEditingMode lastInkCanvasEditingMode = InkCanvasEditingMode.Ink;
        bool isSingleFingerDragMode = false;

        private void inkCanvas_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            dec.Add(e.TouchDevice.Id);
            if (isInMultiTouchMode)
            {
                if (touchInk.AcceptsInk)
                {
                    HidePanelsForInking();
                    touchInk.Begin(e, GetTouchEraser(e));
                }
                return;
            }

            if (IsTwoFingerGestureActive && (dec.Count > 1 || isSingleFingerDragMode) &&
                inkCanvas.EditingMode is not (InkCanvasEditingMode.None or InkCanvasEditingMode.Select))
            {
                lastInkCanvasEditingMode = inkCanvas.EditingMode;
                inkCanvas.EditingMode = InkCanvasEditingMode.None;
            }
        }

        private void inkCanvas_PreviewTouchUp(object sender, TouchEventArgs e)
        {
            if (isInMultiTouchMode)
            {
                try
                {
                    touchInk.Complete(e, stroke => inkCanvas_StrokeCollected(inkCanvas, new InkCanvasStrokeCollectedEventArgs(stroke)));
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
                }
                inkCanvas.Opacity = 1;
                return;
            }
            if (!dec.Contains(e.TouchDevice.Id))
            {
                if (drawingShapeMode != ShapeTool.Pen) e.Handled = true;
                return;
            }
            //手势完成后切回之前的状态
            if (dec.Count > 1)
            {
                if (inkCanvas.EditingMode == InkCanvasEditingMode.None)
                {
                    inkCanvas.EditingMode = lastInkCanvasEditingMode;
                }
            }
            dec.Remove(e.TouchDevice.Id);
            inkCanvas.Opacity = 1;
        }
        private void inkCanvas_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
        {
            e.Mode = ManipulationModes.All;
        }

        private void inkCanvas_ManipulationInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
        {

        }

        private void Main_Grid_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            ToCommitStrokeManipulationHistoryAfterMouseUp();
            CommitPendingStrokeDrawingAttributes();
            if (e.Manipulators.Count() == 0)
            {
                if (forceEraser || isInMultiTouchMode) return;
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            }
        }

        private void Main_Grid_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            if (isInMultiTouchMode || !IsTwoFingerGestureActive) return;
            if ((dec.Count >= 2 && (Settings.PowerPointSettings.IsEnableTwoFingerGestureInPresentationMode || BtnPPTSlideShowEnd.Visibility != Visibility.Visible)) || isSingleFingerDragMode)
            {
                var transform = Matrix.Identity;
                var delta = e.DeltaManipulation;
                var gesture = Settings.Gesture;
                var center = GetMatrixTransformCenterPoint(e.ManipulationOrigin, e.Source as FrameworkElement);
                if (gesture.IsEnableTwoFingerZoom)
                    transform.ScaleAt(delta.Scale.X, delta.Scale.Y, center.X, center.Y);
                if (gesture.IsEnableTwoFingerRotation)
                    transform.RotateAt(delta.Rotation, center.X, center.Y);
                if (IsTwoFingerTranslateActive)
                    transform.Translate(delta.Translation.X, delta.Translation.Y);
                if (transform.IsIdentity) return;

                foreach (UIElement element in FluentCanvasElementsHelper.GetAllElements(inkCanvas))
                    ApplyElementMatrixTransform(element, transform);

                foreach (var stroke in inkCanvas.Strokes)
                {
                    TransformStrokeDuringBoardGesture(stroke, transform);
                    if (!gesture.IsEnableTwoFingerZoom) continue;
                    var attributes = stroke.DrawingAttributes;
                    attributes.Width = Math.Clamp(attributes.Width * delta.Scale.X, DrawingAttributes.MinWidth, DrawingAttributes.MaxWidth);
                    attributes.Height = Math.Clamp(attributes.Height * delta.Scale.Y, DrawingAttributes.MinHeight, DrawingAttributes.MaxHeight);
                }
                foreach (Circle circle in circles)
                {
                    circle.R = GetDistance(circle.Stroke.StylusPoints[0].ToPoint(), circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].ToPoint()) / 2;
                    circle.Centroid = new Point(
                        (circle.Stroke.StylusPoints[0].X + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].X) / 2,
                        (circle.Stroke.StylusPoints[0].Y + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].Y) / 2
                    );
                }
            }
        }
    }
}
