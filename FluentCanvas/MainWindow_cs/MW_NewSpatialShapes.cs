using System;
using FluentCanvas.Helpers;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;

namespace FluentCanvas;

public partial class MainWindow
{
    private void DrawNewSpatialShape(ShapeTool tool, Point start, Point end)
    {
        var ink = inkCanvas.DefaultDrawingAttributes;
        var strokes = tool switch
        {
            ShapeTool.NumberLine => DrawNumberLine(ink, start, end, showLabels: false),
            ShapeTool.NumberLineLabeled => DrawNumberLine(ink, start, end, showLabels: true),
            ShapeTool.Sphere => DrawSphere(ink, start, end),
            ShapeTool.Tetrahedron => DrawTetrahedron(ink, start, end),
            _ => new StrokeCollection()
        };
        SetShapeDraft(strokes);
    }

    private static StrokeCollection DrawNumberLine(DrawingAttributes ink, Point start, Point end, bool showLabels)
    {
        var strokes = new StrokeCollection();
        double left = Math.Min(start.X, end.X);
        double right = Math.Max(start.X, end.X);
        double width = right - left;
        double inkWidth = Math.Max(ink.Width, ink.Height);
        double arrowLength = Math.Clamp(width * 0.12, 9, 18);
        double usableHalfWidth = width / 2 - arrowLength - inkWidth;
        double minimumSpacing = Math.Max(showLabels ? 30 : 24, 2.5 * inkWidth);
        if (usableHalfWidth < minimumSpacing * 0.65) return strokes;

        var leftPoint = new Point(left, start.Y);
        var rightPoint = new Point(right, start.Y);
        var vertical = new Point(left, start.Y + width);
        int ticks = Math.Clamp((int)(usableHalfWidth / minimumSpacing), 1, 9);
        var line = new ParametricInk(GeometryKind.NumberLine, leftPoint, rightPoint, vertical, inkWidth, width,
            Ticks: ticks, Group: Guid.NewGuid());
        strokes.Add(line.Create(ink));
        var labelInk = ink.Clone();
        labelInk.Width = labelInk.Height = Math.Clamp(inkWidth * 0.45, 0.9, 2.0);
        for (int tick = -ticks; tick <= ticks; tick++)
        {
            strokes.Add((line with { Part = 10 + tick }).Create(ink));
            if (!showLabels) continue;
            if (tick < 0) strokes.Add((line with { Part = 50 + tick }).Create(labelInk));
            strokes.Add((line with { Part = 30 + tick }).Create(labelInk));
        }
        return strokes;
    }

    private static StrokeCollection DrawSphere(DrawingAttributes ink, Point center, Point edge)
    {
        var strokes = new StrokeCollection();
        double radius = (edge - center).Length;
        if (radius < Math.Max(8, Math.Max(ink.Width, ink.Height))) return strokes;

        Point Outline(double progress) => AtAngle(Math.Tau * progress, radius);
        Point Front(double progress) => AtAngle(Math.PI * progress, radius * 0.28);
        Point Back(double progress) => AtAngle(Math.PI * (1 + progress), radius * 0.28);
        Point AtAngle(double angle, double verticalRadius) => new(
            center.X + radius * Math.Cos(angle), center.Y + verticalRadius * Math.Sin(angle));

        int segments = Math.Clamp((int)(radius / 2), 32, 160);
        strokes.Add(CreateSpatialShapeStroke(ink, SampleSpatialPath(Outline, segments)));
        strokes.Add(CreateSpatialShapeStroke(ink, SampleSpatialPath(Front, segments / 2)));
        AddSpatialDashes(strokes, ink, radius * 2.2, Back, segmentsPerDash: 6);
        return strokes;
    }

    private static StrokeCollection DrawTetrahedron(DrawingAttributes ink, Point start, Point end)
    {
        var strokes = new StrokeCollection();
        double x = Math.Min(start.X, end.X);
        double y = Math.Min(start.Y, end.Y);
        double width = Math.Abs(end.X - start.X);
        double height = Math.Abs(end.Y - start.Y);
        if (Math.Min(width, height) < Math.Max(12, 2 * Math.Max(ink.Width, ink.Height))) return strokes;

        var apex = new Point(x + width * 0.42, y);
        var left = new Point(x, y + height * 0.84);
        var front = new Point(x + width * 0.72, y + height);
        var right = new Point(x + width, y + height * 0.67);
        strokes.Add(CreateSpatialShapeStroke(ink, [apex, left, front, right, apex]));
        strokes.Add(CreateSpatialShapeStroke(ink, [apex, front]));

        Vector hiddenEdge = right - left;
        AddSpatialDashes(strokes, ink, hiddenEdge.Length, progress => left + hiddenEdge * progress, segmentsPerDash: 1);
        return strokes;
    }

    private static void AddSpatialDashes(StrokeCollection strokes, DrawingAttributes ink, double length,
        Func<double, Point> getPoint, int segmentsPerDash)
    {
        double inkWidth = Math.Max(ink.Width, ink.Height);
        int count = Math.Clamp((int)(length / (3 * inkWidth + 10)), 2, 20);
        for (int dash = 0; dash < count; dash++)
        {
            double start = (dash + 0.20) / count;
            double sweep = 0.55 / count;
            var points = SampleSpatialPath(progress => getPoint(start + sweep * progress), segmentsPerDash);
            strokes.Add(CreateSpatialShapeStroke(ink, points));
        }
    }

    private static Point[] SampleSpatialPath(Func<double, Point> getPoint, int segments)
    {
        var points = new Point[segments + 1];
        for (int i = 0; i <= segments; i++)
            points[i] = getPoint((double)i / segments);
        return points;
    }

    private static Stroke CreateSpatialShapeStroke(DrawingAttributes ink, Point[] points)
    {
        var attributes = ink.Clone();
        attributes.FitToCurve = false;
        return new Stroke(new StylusPointCollection(points), attributes);
    }
}
