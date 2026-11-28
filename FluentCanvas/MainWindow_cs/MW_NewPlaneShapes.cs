using System;
using FluentCanvas.Helpers;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;

namespace FluentCanvas;

public partial class MainWindow
{
    private const double MinimumPlaneShapeLength = 4;

    private StrokeCollection CreatePlaneShape(ShapeTool shape, Point start, Point end, Point? firstPoint = null,
        double? sweepAngle = null)
    {
        if (shape is ShapeTool.Triangle or ShapeTool.AngleMark or ShapeTool.RightAngleMark or ShapeTool.Arc or ShapeTool.Sector)
        {
            if (firstPoint is Point anchor) return CreateThreePointPlaneShape(shape, anchor, start, end, sweepAngle);

            return (end - start).Length >= MinimumPlaneShapeLength
                ? new StrokeCollection { GenerateLineStroke(start, end) }
                : new StrokeCollection();
        }

        double width = end.X - start.X;
        double height = end.Y - start.Y;
        var strokes = new StrokeCollection();
        if (shape == ShapeTool.Semicircle)
        {
            Vector diameter = end - start;
            if (diameter.Length >= 2 * MinimumPlaneShapeLength)
            {
                Point center = start + diameter / 2;
                var v = end - center;
                strokes.Add(new ParametricInk(GeometryKind.Chord, center, end, center + new Vector(-v.Y, v.X),
                    -Math.PI).Create(inkCanvas.DefaultDrawingAttributes));
            }
            return strokes;
        }

        if (Math.Abs(width) < MinimumPlaneShapeLength || Math.Abs(height) < MinimumPlaneShapeLength) return strokes;

        Point[] outline;
        switch (shape)
        {
            case ShapeTool.RightTriangle:
                outline = [start, new Point(start.X, end.Y), end, start];
                break;
            case ShapeTool.IsoscelesTriangle:
            case ShapeTool.EquilateralTriangle:
                double centerX = start.X + width / 2;
                if (shape == ShapeTool.EquilateralTriangle)
                {
                    double heightToWidth = Math.Sqrt(3) / 2;
                    width = Math.Min(Math.Abs(width), Math.Abs(height) / heightToWidth);
                    height = Math.CopySign(width * heightToWidth, height);
                }
                Point apex = new(centerX, start.Y);
                outline = [apex, new Point(centerX - width / 2, start.Y + height),
                    new Point(centerX + width / 2, start.Y + height), apex];
                break;
            case ShapeTool.Parallelogram:
                Point topLeft = new(start.X + width * 0.22, start.Y);
                outline = [topLeft, new Point(end.X, start.Y), new Point(start.X + width * 0.78, end.Y),
                    new Point(start.X, end.Y), topLeft];
                break;
            case ShapeTool.Trapezoid:
                Point narrowLeft = new(start.X + width * 0.2, start.Y);
                outline = [narrowLeft, new Point(start.X + width * 0.8, start.Y), end,
                    new Point(start.X, end.Y), narrowLeft];
                break;
            default:
                return strokes;
        }

        strokes.Add(outline.Length == 4
            ? new ParametricInk(GeometryKind.Triangle, outline[0], outline[1], outline[2]).Create(inkCanvas.DefaultDrawingAttributes)
            : CreatePlaneStroke(outline));
        return strokes;
    }

    private StrokeCollection CreateThreePointPlaneShape(ShapeTool shape, Point anchor, Point start, Point end,
        double? sweepAngle)
    {
        var strokes = new StrokeCollection();
        Vector firstRay = start - anchor;
        Vector secondRay = end - anchor;
        double firstLength = firstRay.Length;
        double secondLength = secondRay.Length;
        if (firstLength < MinimumPlaneShapeLength || secondLength < MinimumPlaneShapeLength) return strokes;

        Vector firstDirection = firstRay / firstLength;
        if (shape == ShapeTool.Triangle)
        {
            double height = Math.Abs(Vector.CrossProduct(firstDirection, secondRay));
            if (height >= MinimumPlaneShapeLength) strokes.Add(new ParametricInk(GeometryKind.Triangle, anchor, start, end).Create(inkCanvas.DefaultDrawingAttributes));
            return strokes;
        }

        Vector secondDirection = secondRay / secondLength;
        double turn = Vector.CrossProduct(firstDirection, secondDirection);
        double dot = Vector.Multiply(firstDirection, secondDirection);
        double radius = shape is ShapeTool.AngleMark or ShapeTool.RightAngleMark
            ? Math.Min(26, Math.Min(firstLength, secondLength) * 0.45)
            : firstLength;

        if (shape == ShapeTool.RightAngleMark)
        {
            if (radius < MinimumPlaneShapeLength || Math.Abs(turn) < 0.1) return strokes;

            Vector firstSide = firstDirection * radius;
            Vector secondSide = new Vector(-firstSide.Y, firstSide.X) * Math.Sign(turn);
            strokes.Add(CreatePlaneStroke(new[] { anchor + firstSide, anchor + firstSide + secondSide, anchor + secondSide }));
            return strokes;
        }

        double sweep = Math.Clamp(sweepAngle ?? Math.Atan2(turn, dot), -Math.Tau, Math.Tau);
        if (Math.Abs(sweep) * radius < MinimumPlaneShapeLength) return strokes;

        var right = anchor + firstDirection * radius;
        var bottom = anchor + new Vector(-firstDirection.Y, firstDirection.X) * radius;
        var kind = shape == ShapeTool.Sector ? GeometryKind.Sector : GeometryKind.Arc;
        strokes.Add(new ParametricInk(kind, anchor, right, bottom, sweep).Create(inkCanvas.DefaultDrawingAttributes));
        return strokes;
    }

    private Stroke CreatePlaneStroke(IEnumerable<Point> points)
    {
        var attributes = inkCanvas.DefaultDrawingAttributes.Clone();
        attributes.FitToCurve = false;
        return new Stroke(new StylusPointCollection(points), attributes);
    }
}
