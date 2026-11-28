using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using iNKORE.UI.WPF.Modern.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        #region Floating Bar Control

        private void ImageDrawShape_Click(object sender, RoutedEventArgs e) =>
            ToggleToolbarPanel(BorderDrawShape, BoardBorderDrawShape);

        #endregion Floating Bar Control

        private ShapeTool activeShapeMode;
        private ShapeTool drawingShapeMode
        {
            get => activeShapeMode;
            set
            {
                if (UsesNewShapeInput || inkCanvas?.HasQueuedGeometry == true || shapeDraft != null || stagedShapeStrokes != null) CancelShapeDraft();
                activeShapeMode = value;
            }
        }
        bool isLongPressSelected = false; // 用于存是否是“选中”状态，便于后期抬笔后不做切换到笔的处理

        #region Buttons

        private void ToggleShapePanelPin(object sender, RoutedEventArgs e)
        {
            bool autoHide = !ToggleSwitchDrawShapeBorderAutoHide.IsOn;
            ToggleSwitchDrawShapeBorderAutoHide.IsOn = autoHide;
            BorderDrawShapePin.Glyph = BoardBorderDrawShapePin.Glyph = autoHide ? "\uE718" : "\uE77a";
            string status = autoHide ? UiText.Get("Actions.AutoHide") : UiText.Get("Actions.Pinned");
            AutomationProperties.SetItemStatus(BoardShapePanelPinButton, status);
            AutomationProperties.SetItemStatus(DesktopShapePanelPinButton, status);
        }

        private long lineShapePressStarted;
        private bool lineShapeHeld;

        private void LineShapeButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            lineShapePressStarted = Stopwatch.GetTimestamp();
            lineShapeHeld = false;
        }

        private void LineShapeButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            lineShapeHeld = lineShapePressStarted != 0 && sender is Button { IsMouseOver: true } &&
                            Stopwatch.GetElapsedTime(lineShapePressStarted) >= TimeSpan.FromMilliseconds(500);
            lineShapePressStarted = 0;
        }

        private void LineShapeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string mode } && Enum.TryParse(mode, out ShapeTool shape) &&
                shape is ShapeTool.Line or ShapeTool.DashedLine or ShapeTool.DottedLine or ShapeTool.ArrowLine or ShapeTool.ParallelLines)
                SelectShapeTool(shape, lineShapeHeld);

            lineShapeHeld = false;
        }

        private void BtnPen_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = false;
            drawingShapeMode = ShapeTool.Pen;
            inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            inkCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
            isLongPressSelected = false;
        }

        private void SelectShapeTool(ShapeTool shape, bool? keepDrawing = null)
        {
            if (isInMultiTouchMode)
            {
                ToggleSwitchEnableMultiTouchMode.IsOn = false;
                lastIsInMultiTouchMode = true;
            }
            forceEraser = true;
            drawingShapeMode = shape;
            inkCanvas.EditingMode = InkCanvasEditingMode.None;
            inkCanvas.IsManipulationEnabled = true;
            CancelSingleFingerDragMode();
            if (keepDrawing.HasValue) isLongPressSelected = keepDrawing.Value;
            DrawShapePromptToPen();
        }

        private void BtnDrawCoordinate1_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Coordinate1);

        private void BtnDrawCoordinate2_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Coordinate2);

        private void BtnDrawCoordinate3_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Coordinate3);

        private void BtnDrawCoordinate4_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Coordinate4);

        private void BtnDrawCoordinate5_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Coordinate5);

        private void BtnDrawRectangle_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Rectangle);

        private void BtnDrawRectangleCenter_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.CenterRectangle);

        private void BtnDrawEllipse_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Ellipse, false);

        private void BtnDrawCircle_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Circle);

        private void BtnDrawCenterEllipse_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.CenterEllipse);

        private void BtnDrawCenterEllipseWithFocalPoint_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.CenterEllipseWithFocalPoints);

        private void BtnDrawDashedCircle_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.DashedCircle);

        private void BtnDrawHyperbola_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Hyperbola);

        private void BtnDrawHyperbolaWithFocalPoint_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.HyperbolaWithFocalPoints);

        private void BtnDrawParabola1_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Parabola);

        private void BtnDrawParabolaWithFocalPoint_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.ParabolaWithFocalPoint);

        private void BtnDrawParabola2_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.SidewaysParabola);

        private void BtnDrawCylinder_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Cylinder);

        private void BtnDrawCone_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Cone);

        private void BtnDrawCuboid_Click(object sender, RoutedEventArgs e) =>
            SelectShapeTool(ShapeTool.Cuboid);

        #endregion

        private void inkCanvas_TouchMove(object sender, TouchEventArgs e)
        {
            if (isSingleFingerDragMode || !dec.Contains(e.TouchDevice.Id)) return;
            if (drawingShapeMode != ShapeTool.Pen)
            {
                if (isLastTouchEraser)
                {
                    return;
                }
                //EraserContainer.Background = null;
                //ImageEraser.Visibility = Visibility.Visible;
                if (isWaitUntilNextTouchDown) return;
                if (dec.Count > 1)
                {
                    CancelShapeDraft();
                    isWaitUntilNextTouchDown = true;
                    return;
                }
                if (inkCanvas.EditingMode != InkCanvasEditingMode.None)
                {
                    inkCanvas.EditingMode = InkCanvasEditingMode.None;
                }
            }
            QueueShapePreview(e.GetTouchPoint(inkCanvas).Position);
        }

        private Action<Point> buildShapePreview;

        private void QueueShapePreview(Point position)
        {
            if (drawingShapeMode == ShapeTool.Pen) return;
            // Arc direction accumulates across input packets even between rendered frames.
            if (UsesNewShapeInput) TrackArcSweep(position);
            inkCanvas.QueueGeometry(position, buildShapePreview ??= BuildShapePreview);
        }

        private void BuildShapePreview(Point point)
        {
            if (UsesNewShapeInput) UpdateNewShape(point);
            else MouseTouchMove(drawingShapeMode is ShapeTool.Circle or ShapeTool.CenterEllipse or
                ShapeTool.Coordinate1 or ShapeTool.Coordinate2 or ShapeTool.Coordinate3 or ShapeTool.Coordinate4 or ShapeTool.Coordinate5
                ? SnapShapePoint(point) : point);
        }

        private double hyperbolaAsymptoteSlope;

        private void MouseTouchMove(Point endP)
        {
            if (drawingShapeMode == ShapeTool.Pen) return;

            List<System.Windows.Point> pointList;
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            Point newIniP = iniP;
            switch (drawingShapeMode)
            {
                case ShapeTool.Line:
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(iniP.X, iniP.Y),
                        new System.Windows.Point(endP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    SetShapeDraft(stroke);
                    break;
                case ShapeTool.DashedLine:
                    strokes.Add(GenerateDashedLineStrokeCollection(iniP, endP));
                    SetShapeDraft(strokes);
                    break;
                case ShapeTool.DottedLine:
                    strokes.Add(GenerateDotLineStrokeCollection(iniP, endP));
                    SetShapeDraft(strokes);
                    break;
                case ShapeTool.ArrowLine:
                    double w = 30, h = 10;
                    double theta = Math.Atan2(iniP.Y - endP.Y, iniP.X - endP.X);
                    double sint = Math.Sin(theta);
                    double cost = Math.Cos(theta);

                    pointList = new List<Point>
                    {
                        new Point(iniP.X, iniP.Y),
                        new Point(endP.X , endP.Y),
                        new Point(endP.X + (w * cost - h * sint), endP.Y + (w * sint + h * cost)),
                        new Point(endP.X,endP.Y),
                        new Point(endP.X + (w * cost + h * sint), endP.Y - (h * cost - w * sint))
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    SetShapeDraft(stroke);

                    break;
                case ShapeTool.ParallelLines:
                    double d = GetDistance(iniP, endP);
                    if (d == 0) return;
                    double sinTheta = (iniP.Y - endP.Y) / d;
                    double cosTheta = (endP.X - iniP.X) / d;
                    double tanTheta = Math.Abs(sinTheta / cosTheta);
                    double x = 25;
                    if (Math.Abs(tanTheta) < 1.0 / 12)
                    {
                        sinTheta = 0;
                        cosTheta = 1;
                        endP.Y = iniP.Y;
                    }
                    if (tanTheta < 0.63 && tanTheta > 0.52) //30
                    {
                        sinTheta = sinTheta / Math.Abs(sinTheta) * 0.5;
                        cosTheta = cosTheta / Math.Abs(cosTheta) * 0.866;
                        endP.Y = iniP.Y - d * sinTheta;
                        endP.X = iniP.X + d * cosTheta;
                    }
                    if (tanTheta < 1.08 && tanTheta > 0.92) //45
                    {
                        sinTheta = sinTheta / Math.Abs(sinTheta) * 0.707;
                        cosTheta = cosTheta / Math.Abs(cosTheta) * 0.707;
                        endP.Y = iniP.Y - d * sinTheta;
                        endP.X = iniP.X + d * cosTheta;
                    }
                    if (tanTheta < 1.95 && tanTheta > 1.63) //60
                    {
                        sinTheta = sinTheta / Math.Abs(sinTheta) * 0.866;
                        cosTheta = cosTheta / Math.Abs(cosTheta) * 0.5;
                        endP.Y = iniP.Y - d * sinTheta;
                        endP.X = iniP.X + d * cosTheta;
                    }
                    if (Math.Abs(cosTheta / sinTheta) < 1.0 / 12)
                    {
                        endP.X = iniP.X;
                        sinTheta = 1;
                        cosTheta = 0;
                    }
                    strokes.Add(GenerateLineStroke(new Point(iniP.X - 3 * x * sinTheta, iniP.Y - 3 * x * cosTheta), new Point(endP.X - 3 * x * sinTheta, endP.Y - 3 * x * cosTheta)));
                    strokes.Add(GenerateLineStroke(new Point(iniP.X - x * sinTheta, iniP.Y - x * cosTheta), new Point(endP.X - x * sinTheta, endP.Y - x * cosTheta)));
                    strokes.Add(GenerateLineStroke(new Point(iniP.X + x * sinTheta, iniP.Y + x * cosTheta), new Point(endP.X + x * sinTheta, endP.Y + x * cosTheta)));
                    strokes.Add(GenerateLineStroke(new Point(iniP.X + 3 * x * sinTheta, iniP.Y + 3 * x * cosTheta), new Point(endP.X + 3 * x * sinTheta, endP.Y + 3 * x * cosTheta)));
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Coordinate1:
                    strokes.Add(CreateCoordinateAxis(new Point(2 * iniP.X - (endP.X - 20), iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(CreateCoordinateAxis(new Point(iniP.X, 2 * iniP.Y - (endP.Y + 20)), new Point(iniP.X, endP.Y)));
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Coordinate2:
                    if (Math.Abs(iniP.X - endP.X) < 0.01) return;
                    strokes.Add(CreateCoordinateAxis(new Point(iniP.X + (iniP.X - endP.X) / Math.Abs(iniP.X - endP.X) * 25, iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(CreateCoordinateAxis(new Point(iniP.X, 2 * iniP.Y - (endP.Y + 20)), new Point(iniP.X, endP.Y)));
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Coordinate3:
                    if (Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    strokes.Add(CreateCoordinateAxis(new Point(2 * iniP.X - (endP.X - 20), iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(CreateCoordinateAxis(new Point(iniP.X, iniP.Y + (iniP.Y - endP.Y) / Math.Abs(iniP.Y - endP.Y) * 25), new Point(iniP.X, endP.Y)));
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Coordinate4:
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    strokes.Add(CreateCoordinateAxis(new Point(iniP.X + (iniP.X - endP.X) / Math.Abs(iniP.X - endP.X) * 25, iniP.Y), new Point(endP.X, iniP.Y)));
                    strokes.Add(CreateCoordinateAxis(new Point(iniP.X, iniP.Y + (iniP.Y - endP.Y) / Math.Abs(iniP.Y - endP.Y) * 25), new Point(iniP.X, endP.Y)));
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Coordinate5:
                    strokes.Add(CreateCoordinateAxis(new Point(iniP.X, iniP.Y), new Point(iniP.X + Math.Abs(endP.X - iniP.X), iniP.Y)));
                    strokes.Add(CreateCoordinateAxis(new Point(iniP.X, iniP.Y), new Point(iniP.X, iniP.Y - Math.Abs(endP.Y - iniP.Y))));
                    d = (Math.Abs(iniP.X - endP.X) + Math.Abs(iniP.Y - endP.Y)) / 2;
                    strokes.Add(CreateCoordinateAxis(new Point(iniP.X, iniP.Y), new Point(iniP.X - d / 1.76, iniP.Y + d / 1.76)));
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Rectangle:
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(iniP.X, iniP.Y),
                        new System.Windows.Point(iniP.X, endP.Y),
                        new System.Windows.Point(endP.X, endP.Y),
                        new System.Windows.Point(endP.X, iniP.Y),
                        new System.Windows.Point(iniP.X, iniP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    SetShapeDraft(stroke);

                    break;
                case ShapeTool.CenterRectangle:
                    double a = iniP.X - endP.X;
                    double b = iniP.Y - endP.Y;
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(iniP.X - a, iniP.Y - b),
                        new System.Windows.Point(iniP.X - a, iniP.Y + b),
                        new System.Windows.Point(iniP.X + a, iniP.Y + b),
                        new System.Windows.Point(iniP.X + a, iniP.Y - b),
                        new System.Windows.Point(iniP.X - a, iniP.Y - b)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    SetShapeDraft(stroke);

                    break;
                case ShapeTool.Ellipse:
                    SetShapeDraft(ParametricInk.Ellipse(iniP, endP).Create(inkCanvas.DefaultDrawingAttributes));
                    break;
                case ShapeTool.Circle:
                    double R = GetDistance(iniP, endP);
                    SetShapeDraft(new ParametricInk(GeometryKind.Arc, iniP, new Point(iniP.X + R, iniP.Y),
                        new Point(iniP.X, iniP.Y + R), Math.Tau).Create(inkCanvas.DefaultDrawingAttributes));
                    break;
                case ShapeTool.CenterEllipse:
                    SetShapeDraft(new ParametricInk(GeometryKind.Arc, iniP, new Point(endP.X, iniP.Y),
                        new Point(iniP.X, endP.Y), Math.Tau).Create(inkCanvas.DefaultDrawingAttributes));
                    break;
                case ShapeTool.CenterEllipseWithFocalPoints:
                    a = Math.Abs(endP.X - iniP.X);
                    b = Math.Abs(endP.Y - iniP.Y);
                    pointList = GenerateEllipseGeometry(new Point(iniP.X - a, iniP.Y - b), new Point(iniP.X + a, iniP.Y + b));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke);
                    double c = Math.Sqrt(Math.Abs(a * a - b * b));
                    StylusPoint stylusPoint;
                    if (a > b)
                    {
                        stylusPoint = new StylusPoint(iniP.X + c, iniP.Y, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                        };
                        strokes.Add(stroke.Clone());
                        stylusPoint = new StylusPoint(iniP.X - c, iniP.Y, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                        };
                        strokes.Add(stroke.Clone());
                    }
                    else if (a < b)
                    {
                        stylusPoint = new StylusPoint(iniP.X, iniP.Y - c, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                        };
                        strokes.Add(stroke.Clone());
                        stylusPoint = new StylusPoint(iniP.X, iniP.Y + c, (float)1.0);
                        point = new StylusPointCollection();
                        point.Add(stylusPoint);
                        stroke = new Stroke(point)
                        {
                            DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                        };
                        strokes.Add(stroke.Clone());
                    }
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.DashedCircle:
                    R = GetDistance(iniP, endP);
                    strokes = GenerateDashedLineEllipseStrokeCollection(new Point(iniP.X - R, iniP.Y - R), new Point(iniP.X + R, iniP.Y + R));
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Hyperbola:
                case ShapeTool.HyperbolaWithFocalPoints:
                    //双曲线 x^2/a^2 - y^2/b^2 = 1
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    var pointList2 = new List<Point>();
                    var pointList3 = new List<Point>();
                    var pointList4 = new List<Point>();
                    if (stagedShapeStrokes == null)
                    {
                        //第一笔：画渐近线
                        double k = Math.Abs((endP.Y - iniP.Y) / (endP.X - iniP.X));
                        strokes.Add(GenerateDashedLineStrokeCollection(new Point(2 * iniP.X - endP.X, 2 * iniP.Y - endP.Y), endP));
                        strokes.Add(GenerateDashedLineStrokeCollection(new Point(2 * iniP.X - endP.X, endP.Y), new Point(endP.X, 2 * iniP.Y - endP.Y)));
                        hyperbolaAsymptoteSlope = k;
                    }
                    else
                    {
                        //第二笔：画双曲线
                        double k = hyperbolaAsymptoteSlope;
                        bool isHyperbolaFocalPointOnXAxis = Math.Abs((endP.Y - iniP.Y) / (endP.X - iniP.X)) < k;
                        if (isHyperbolaFocalPointOnXAxis)
                        { // 焦点在 x 轴上
                            a = Math.Sqrt(Math.Abs((endP.X - iniP.X) * (endP.X - iniP.X) - (endP.Y - iniP.Y) * (endP.Y - iniP.Y) / (k * k)));
                            b = a * k;
                            pointList = new List<Point>();
                            for (double i = a; i <= Math.Abs(endP.X - iniP.X); i += 0.5)
                            {
                                double rY = Math.Sqrt(Math.Abs(k * k * i * i - b * b));
                                pointList.Add(new Point(iniP.X + i, iniP.Y - rY));
                                pointList2.Add(new Point(iniP.X + i, iniP.Y + rY));
                                pointList3.Add(new Point(iniP.X - i, iniP.Y - rY));
                                pointList4.Add(new Point(iniP.X - i, iniP.Y + rY));
                            }
                        }
                        else
                        { // 焦点在 y 轴上
                            a = Math.Sqrt(Math.Abs((endP.Y - iniP.Y) * (endP.Y - iniP.Y) - (endP.X - iniP.X) * (endP.X - iniP.X) * (k * k)));
                            b = a / k;
                            pointList = new List<Point>();
                            for (double i = a; i <= Math.Abs(endP.Y - iniP.Y); i += 0.5)
                            {
                                double rX = Math.Sqrt(Math.Abs(i * i / k / k - b * b));
                                pointList.Add(new Point(iniP.X - rX, iniP.Y + i));
                                pointList2.Add(new Point(iniP.X + rX, iniP.Y + i));
                                pointList3.Add(new Point(iniP.X - rX, iniP.Y - i));
                                pointList4.Add(new Point(iniP.X + rX, iniP.Y - i));
                            }
                        }
                        try
                        {
                            point = new StylusPointCollection(pointList);
                            stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                            strokes.Add(stroke.Clone());
                            point = new StylusPointCollection(pointList2);
                            stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                            strokes.Add(stroke.Clone());
                            point = new StylusPointCollection(pointList3);
                            stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                            strokes.Add(stroke.Clone());
                            point = new StylusPointCollection(pointList4);
                            stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                            strokes.Add(stroke.Clone());
                            if (drawingShapeMode == ShapeTool.HyperbolaWithFocalPoints)
                            {
                                //画焦点
                                c = Math.Sqrt(a * a + b * b);
                                stylusPoint = isHyperbolaFocalPointOnXAxis ? new StylusPoint(iniP.X + c, iniP.Y, (float)1.0) : new StylusPoint(iniP.X, iniP.Y + c, (float)1.0);
                                point = new StylusPointCollection();
                                point.Add(stylusPoint);
                                stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                                strokes.Add(stroke.Clone());
                                stylusPoint = isHyperbolaFocalPointOnXAxis ? new StylusPoint(iniP.X - c, iniP.Y, (float)1.0) : new StylusPoint(iniP.X, iniP.Y - c, (float)1.0);
                                point = new StylusPointCollection();
                                point.Add(stylusPoint);
                                stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                                strokes.Add(stroke.Clone());
                            }
                        }
                        catch
                        {
                            return;
                        }
                    }
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Parabola:
                    //抛物线 y=ax^2
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    a = (iniP.Y - endP.Y) / ((iniP.X - endP.X) * (iniP.X - endP.X));
                    pointList = new List<Point>();
                    pointList2 = new List<Point>();
                    for (double i = 0.0; i <= Math.Abs(endP.X - iniP.X); i += 0.5)
                    {
                        pointList.Add(new Point(iniP.X + i, iniP.Y - a * i * i));
                        pointList2.Add(new Point(iniP.X - i, iniP.Y - a * i * i));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    point = new StylusPointCollection(pointList2);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.SidewaysParabola:
                    //抛物线 y^2=ax
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    a = (iniP.X - endP.X) / ((iniP.Y - endP.Y) * (iniP.Y - endP.Y));
                    pointList = new List<Point>();
                    pointList2 = new List<Point>();
                    for (double i = 0.0; i <= Math.Abs(endP.Y - iniP.Y); i += 0.5)
                    {
                        pointList.Add(new Point(iniP.X - a * i * i, iniP.Y + i));
                        pointList2.Add(new Point(iniP.X - a * i * i, iniP.Y - i));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    point = new StylusPointCollection(pointList2);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.ParabolaWithFocalPoint:
                    //抛物线 y^2=ax, 含焦点
                    if (Math.Abs(iniP.X - endP.X) < 0.01 || Math.Abs(iniP.Y - endP.Y) < 0.01) return;
                    double p = (iniP.Y - endP.Y) * (iniP.Y - endP.Y) / (2 * (iniP.X - endP.X));
                    a = 0.5 / p;
                    pointList = new List<Point>();
                    pointList2 = new List<Point>();
                    for (double i = 0.0; i <= Math.Abs(endP.Y - iniP.Y); i += 0.5)
                    {
                        pointList.Add(new Point(iniP.X - a * i * i, iniP.Y + i));
                        pointList2.Add(new Point(iniP.X - a * i * i, iniP.Y - i));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    point = new StylusPointCollection(pointList2);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    stylusPoint = new StylusPoint(iniP.X - p / 2, iniP.Y, (float)1.0);
                    point = new StylusPointCollection();
                    point.Add(stylusPoint);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Cylinder:
                    newIniP = iniP;
                    if (iniP.Y > endP.Y)
                    {
                        newIniP = new Point(iniP.X, endP.Y);
                        endP = new Point(endP.X, iniP.Y);
                    }
                    double topA = Math.Abs(newIniP.X - endP.X);
                    double topB = topA / 2.646;
                    //顶部椭圆
                    pointList = GenerateEllipseGeometry(new Point(newIniP.X, newIniP.Y - topB / 2), new Point(endP.X, newIniP.Y + topB / 2));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    //底部椭圆
                    pointList = GenerateEllipseGeometry(new Point(newIniP.X, endP.Y - topB / 2), new Point(endP.X, endP.Y + topB / 2), false, true);
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    strokes.Add(GenerateDashedLineEllipseStrokeCollection(new Point(newIniP.X, endP.Y - topB / 2), new Point(endP.X, endP.Y + topB / 2), true, false));
                    //左侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(newIniP.X, newIniP.Y),
                        new System.Windows.Point(newIniP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    //右侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point(endP.X, newIniP.Y),
                        new System.Windows.Point(endP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Cone:
                    if (iniP.Y > endP.Y)
                    {
                        newIniP = new Point(iniP.X, endP.Y);
                        endP = new Point(endP.X, iniP.Y);
                    }
                    double bottomA = Math.Abs(newIniP.X - endP.X);
                    double bottomB = bottomA / 2.646;
                    //底部椭圆
                    pointList = GenerateEllipseGeometry(new Point(newIniP.X, endP.Y - bottomB / 2), new Point(endP.X, endP.Y + bottomB / 2), false, true);
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    strokes.Add(GenerateDashedLineEllipseStrokeCollection(new Point(newIniP.X, endP.Y - bottomB / 2), new Point(endP.X, endP.Y + bottomB / 2), true, false));
                    //左侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point((newIniP.X + endP.X) / 2, newIniP.Y),
                        new System.Windows.Point(newIniP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    //右侧
                    pointList = new List<System.Windows.Point>{
                        new System.Windows.Point((newIniP.X + endP.X) / 2, newIniP.Y),
                        new System.Windows.Point(endP.X, endP.Y)
                    };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    SetShapeDraft(strokes);

                    break;
                case ShapeTool.Cuboid:
                    if (isFirstTouchCuboid)
                    {
                        //分开画线条方便后期单独擦除某一条棱
                        strokes.Add(GenerateLineStroke(new Point(iniP.X, iniP.Y), new Point(iniP.X, endP.Y)));
                        strokes.Add(GenerateLineStroke(new Point(iniP.X, endP.Y), new Point(endP.X, endP.Y)));
                        strokes.Add(GenerateLineStroke(new Point(endP.X, endP.Y), new Point(endP.X, iniP.Y)));
                        strokes.Add(GenerateLineStroke(new Point(iniP.X, iniP.Y), new Point(endP.X, iniP.Y)));
                        SetShapeDraft(strokes);
                        CuboidFrontRectIniP = iniP;
                        CuboidFrontRectEndP = endP;
                    }
                    else
                    {
                        d = CuboidFrontRectIniP.Y - endP.Y;
                        if (d < 0) d = -d; //就是懒不想做反向的，不要让我去做，想做自己做好之后 Pull Request
                        a = CuboidFrontRectEndP.X - CuboidFrontRectIniP.X; //正面矩形长
                        b = CuboidFrontRectEndP.Y - CuboidFrontRectIniP.Y; //正面矩形宽

                        //横上
                        Point newLineIniP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectIniP.Y - d);
                        Point newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectIniP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        //横下 (虚线)
                        newLineIniP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectEndP.Y - d);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectEndP.Y - d);
                        strokes.Add(GenerateDashedLineStrokeCollection(newLineIniP, newLineEndP));
                        //斜左上
                        newLineIniP = new Point(CuboidFrontRectIniP.X, CuboidFrontRectIniP.Y);
                        newLineEndP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectIniP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        //斜右上
                        newLineIniP = new Point(CuboidFrontRectEndP.X, CuboidFrontRectIniP.Y);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectIniP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        //斜左下 (虚线)
                        newLineIniP = new Point(CuboidFrontRectIniP.X, CuboidFrontRectEndP.Y);
                        newLineEndP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectEndP.Y - d);
                        strokes.Add(GenerateDashedLineStrokeCollection(newLineIniP, newLineEndP));
                        //斜右下
                        newLineIniP = new Point(CuboidFrontRectEndP.X, CuboidFrontRectEndP.Y);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectEndP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        //竖左 (虚线)
                        newLineIniP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectIniP.Y - d);
                        newLineEndP = new Point(CuboidFrontRectIniP.X + d, CuboidFrontRectEndP.Y - d);
                        strokes.Add(GenerateDashedLineStrokeCollection(newLineIniP, newLineEndP));
                        //竖右
                        newLineIniP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectIniP.Y - d);
                        newLineEndP = new Point(CuboidFrontRectEndP.X + d, CuboidFrontRectEndP.Y - d);
                        pointList = new List<System.Windows.Point> { newLineIniP, newLineEndP };
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone() };
                        strokes.Add(stroke.Clone());

                        SetShapeDraft(strokes);
                    }

                    break;
            }
        }

        private bool isFirstTouchCuboid => stagedShapeStrokes == null;
        Point CuboidFrontRectIniP = new Point();
        Point CuboidFrontRectEndP = new Point();

        private void Main_Grid_TouchUp(object sender, TouchEventArgs e)
        {
            if (!isLastTouchEraser) SetShapeReleasePoint(e.GetTouchPoint(inkCanvas).Position);
            inkCanvas_MouseUp(sender, null);
            if (dec.Count == 0)
            {
                isWaitUntilNextTouchDown = false;
            }
        }
        private StrokeCollection shapeDraft;
        private StrokeCollection stagedShapeStrokes;
        bool isWaitUntilNextTouchDown = false;

        private void SetShapeReleasePoint(Point position)
        {
            if (!UsesNewShapeInput && !isWaitUntilNextTouchDown && (shapeDraft != null || inkCanvas.HasQueuedGeometry))
                QueueShapePreview(position);
        }

        private void SetShapeDraft(Stroke stroke) => SetShapeDraft(new StrokeCollection { stroke });

        private void SetShapeDraft(StrokeCollection strokes)
        {
            if (strokes.Count == 0)
            {
                shapeDraft = null;
                inkCanvas.SetShapePreview(stagedShapeStrokes ?? newShapeGuide, null);
                return;
            }
            if (shapeDraft == null && newShapeGuide == null) InvalidateInkRecognition();
            if (!isHidingSubPanelsWhenInking)
            {
                isHidingSubPanelsWhenInking = true;
                HideSubPanels();
            }
            shapeDraft = strokes;
            inkCanvas.SetShapePreview(stagedShapeStrokes, shapeDraft);
        }

        private IEnumerable<Stroke> EnumerateCommittedStrokes() => inkCanvas.Strokes;

        private void CancelShapeDraft()
        {
            shapeSnapPoints = null;
            inkCanvas?.CancelQueuedGeometry();
            ResetNewShapeInput();
            isMouseDown = false;
            isWaitUntilNextTouchDown = false;
            shapeDraft = null;
            stagedShapeStrokes = null;
            inkCanvas?.SetShapePreview(null, null);
        }

        private void CompleteShapeDraft()
        {
            inkCanvas.FlushQueuedGeometry();
            var draft = shapeDraft;
            if (draft == null) return;
            if (draft.Count == 0 || isWaitUntilNextTouchDown)
            {
                shapeDraft = null;
                inkCanvas.SetShapePreview(stagedShapeStrokes, null);
                return;
            }

            bool isCuboid = drawingShapeMode == ShapeTool.Cuboid;
            bool isHyperbola = drawingShapeMode is ShapeTool.Hyperbola or ShapeTool.HyperbolaWithFocalPoints;
            if ((isCuboid || isHyperbola) && stagedShapeStrokes == null)
            {
                if (isCuboid)
                {
                    Point frontStart = new Point(Math.Min(CuboidFrontRectIniP.X, CuboidFrontRectEndP.X),
                        Math.Min(CuboidFrontRectIniP.Y, CuboidFrontRectEndP.Y));
                    CuboidFrontRectEndP = new Point(Math.Max(CuboidFrontRectIniP.X, CuboidFrontRectEndP.X),
                        Math.Max(CuboidFrontRectIniP.Y, CuboidFrontRectEndP.Y));
                    CuboidFrontRectIniP = frontStart;
                }
                stagedShapeStrokes = draft;
                shapeDraft = null;
                inkCanvas.SetShapePreview(stagedShapeStrokes, null);
                return;
            }

            bool keepStaged = isCuboid;
            var staged = stagedShapeStrokes;
            if (isHyperbola && staged != null)
            {
                var option = Settings.Canvas.HyperbolaAsymptoteOption;
                keepStaged = option == OptionalOperation.Yes;
                if (option == OptionalOperation.Ask)
                {
                    long context = canvasContextVersion;
                    keepStaged = MessageBox.Show(this, UiText.Get("Actions.RemoveAsymptotes"), "FluentCanvas",
                        MessageBoxButton.YesNo) != MessageBoxResult.Yes;
                    if (context != canvasContextVersion || !ReferenceEquals(shapeDraft, draft) ||
                        !ReferenceEquals(stagedShapeStrokes, staged)) return;
                }
            }

            StrokeCollection completed = draft;
            if (keepStaged && staged != null)
            {
                completed = new StrokeCollection(staged);
                completed.Add(draft);
            }
            bool isCircle = drawingShapeMode == ShapeTool.Circle;
            foreach (var stroke in completed)
                if (stroke.StylusPoints.Count >= 50 && !stroke.ContainsPropertyData(NativeStrokeCodec.ShapeProperty)) stroke.AddPropertyData(NativeStrokeCodec.ShapeProperty, true);
            CancelShapeDraft();
            var previous = _currentCommitType;
            _currentCommitType = CommitReason.ShapeDrawing;
            try
            {
                inkCanvas.Strokes.Add(completed);
                timeMachine.CommitStrokeUserInputHistory(completed);
            }
            finally { _currentCommitType = previous; }

            if (isCircle)
            {
                var stroke = draft[0];
                var start = stroke.StylusPoints[0].ToPoint();
                var opposite = stroke.StylusPoints[stroke.StylusPoints.Count / 2].ToPoint();
                circles.Add(new Circle(new Point((start.X + opposite.X) / 2, (start.Y + opposite.Y) / 2),
                    GetDistance(start, opposite) / 2, stroke));
            }

            bool resetTool = !isLongPressSelected || isCuboid || isHyperbola;
            if (resetTool) BtnPen_Click(null, null);
            if (lastIsInMultiTouchMode && (resetTool || isCircle))
                RestoreMultiTouchAfterShape();
        }

        private List<System.Windows.Point> GenerateEllipseGeometry(System.Windows.Point st, System.Windows.Point ed, bool isDrawTop = true, bool isDrawBottom = true)
        {
            double a = 0.5 * (ed.X - st.X);
            double b = 0.5 * (ed.Y - st.Y);
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            if (isDrawTop && isDrawBottom)
            {
                for (double r = 0; r <= 2 * Math.PI; r = r + 0.01)
                {
                    pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                }
            }
            else
            {
                if (isDrawBottom)
                {
                    for (double r = 0; r <= Math.PI; r = r + 0.01)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                }
                if (isDrawTop)
                {
                    for (double r = Math.PI; r <= 2 * Math.PI; r = r + 0.01)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                }
            }
            return pointList;
        }

        private StrokeCollection GenerateDashedLineEllipseStrokeCollection(System.Windows.Point st, System.Windows.Point ed, bool isDrawTop = true, bool isDrawBottom = true)
        {
            double a = 0.5 * (ed.X - st.X);
            double b = 0.5 * (ed.Y - st.Y);
            double step = 0.05;
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            if (isDrawBottom)
            {
                for (double i = 0.0; i < 1.0; i += step * 1.66)
                {
                    pointList = new List<Point>();
                    for (double r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                }
            }
            if (isDrawTop)
            {
                for (double i = 1.0; i < 2.0; i += step * 1.66)
                {
                    pointList = new List<Point>();
                    for (double r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01)
                    {
                        pointList.Add(new System.Windows.Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    }
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                }
            }
            return strokes;
        }

        private Stroke GenerateLineStroke(System.Windows.Point st, System.Windows.Point ed)
        {
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            pointList = new List<System.Windows.Point>{
                new System.Windows.Point(st.X, st.Y),
                new System.Windows.Point(ed.X, ed.Y)
            };
            point = new StylusPointCollection(pointList);
            stroke = new Stroke(point)
            {
                DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
            };
            return stroke;
        }

        private Stroke CreateCoordinateAxis(Point start, Point end) =>
            ParametricInk.Arrow(start, end).Create(inkCanvas.DefaultDrawingAttributes);

        private Stroke GenerateArrowLineStroke(System.Windows.Point st, System.Windows.Point ed)
        {
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;

            double w = 20, h = 7;
            double theta = Math.Atan2(st.Y - ed.Y, st.X - ed.X);
            double sint = Math.Sin(theta);
            double cost = Math.Cos(theta);

            pointList = new List<Point>
            {
                new Point(st.X, st.Y),
                new Point(ed.X , ed.Y),
                new Point(ed.X + (w * cost - h * sint), ed.Y + (w * sint + h * cost)),
                new Point(ed.X,ed.Y),
                new Point(ed.X + (w * cost + h * sint), ed.Y - (h * cost - w * sint))
            };
            point = new StylusPointCollection(pointList);
            stroke = new Stroke(point)
            {
                DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
            };
            return stroke;
        }

        private StrokeCollection GenerateDashedLineStrokeCollection(System.Windows.Point st, System.Windows.Point ed)
        {
            double step = 5;
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            double d = GetDistance(st, ed);
            double sinTheta = (ed.Y - st.Y) / d;
            double cosTheta = (ed.X - st.X) / d;
            for (double i = 0.0; i < d; i += step * 2.76)
            {
                pointList = new List<System.Windows.Point>{
                    new System.Windows.Point(st.X + i * cosTheta, st.Y + i * sinTheta),
                    new System.Windows.Point(st.X + Math.Min(i + step, d) * cosTheta, st.Y + Math.Min(i + step, d) * sinTheta)
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
            }
            return strokes;
        }

        private StrokeCollection GenerateDotLineStrokeCollection(System.Windows.Point st, System.Windows.Point ed)
        {
            double step = 3;
            List<System.Windows.Point> pointList = new List<System.Windows.Point>();
            StylusPointCollection point;
            Stroke stroke;
            StrokeCollection strokes = new StrokeCollection();
            double d = GetDistance(st, ed);
            double sinTheta = (ed.Y - st.Y) / d;
            double cosTheta = (ed.X - st.X) / d;
            for (double i = 0.0; i < d; i += step * 2.76)
            {
                var stylusPoint = new StylusPoint(st.X + i * cosTheta, st.Y + i * sinTheta, (float)0.8);
                point = new StylusPointCollection();
                point.Add(stylusPoint);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
            }
            return strokes;
        }

        bool isMouseDown = false;
        private void inkCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (UsesNewShapeInput)
            {
                if (e.ChangedButton != MouseButton.Left || IsTouchPromotedMouse(e) || newShapeTouch != null ||
                    !inkCanvas.CaptureMouse()) return;
                e.Handled = true;
                isMouseDown = true;
                BeginNewShape(e.GetPosition(inkCanvas));
                return;
            }
            isMouseDown = true;
            if (NeedUpdateIniP())
            {
                iniP = e.GetPosition(inkCanvas);
            }
        }

        private void inkCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (UsesNewShapeInput)
            {
                if (!IsTouchPromotedMouse(e) && newShapeTouch == null && (isMouseDown || newShapeFirstPoint.HasValue))
                    QueueShapePreview(e.GetPosition(inkCanvas));
                return;
            }
            if (isMouseDown)
            {
                QueueShapePreview(e.GetPosition(inkCanvas));
            }
        }

        private void inkCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (UsesNewShapeInput && e != null)
            {
                if (e.ChangedButton != MouseButton.Left || IsTouchPromotedMouse(e) || !isMouseDown) return;
                e.Handled = true;
                isMouseDown = false;
                if (inkCanvas.IsMouseCaptured) inkCanvas.ReleaseMouseCapture();
                FinishNewShape(e.GetPosition(inkCanvas));
                return;
            }
            if (isMouseDown && e != null) SetShapeReleasePoint(e.GetPosition(inkCanvas));
            isMouseDown = false;
            CommitPendingStrokeErasure();
            CompleteShapeDraft();

            CommitPendingStrokeDrawingAttributes();
        }

        private bool NeedUpdateIniP() => stagedShapeStrokes == null ||
            drawingShapeMode is not (ShapeTool.Hyperbola or ShapeTool.HyperbolaWithFocalPoints);
    }
}
