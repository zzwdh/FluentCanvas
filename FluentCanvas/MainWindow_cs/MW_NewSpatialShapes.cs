using System;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;

namespace FluentCanvas;

public partial class MainWindow
{
    // Each digit is a single ink path, so labels participate in undo and document saving.
    private static readonly (double X, double Y)[][] NumberLineDigits =
    [
        [(0.25, 0), (0.75, 0), (1, 0.2), (1, 0.8), (0.75, 1), (0.25, 1), (0, 0.8), (0, 0.2), (0.25, 0)],
        [(0.15, 0.22), (0.55, 0), (0.55, 1)],
        [(0, 0.2), (0.25, 0), (0.75, 0), (1, 0.2), (1, 0.35), (0, 1), (1, 1)],
        [(0, 0.08), (0.35, 0), (0.75, 0), (1, 0.18), (1, 0.33), (0.75, 0.47), (0.38, 0.47),
            (0.75, 0.47), (1, 0.65), (1, 0.82), (0.75, 1), (0.3, 1), (0, 0.91)],
        [(0.78, 1), (0.78, 0), (0, 0.66), (1, 0.66)],
        [(1, 0), (0.14, 0), (0, 0.46), (0.68, 0.46), (1, 0.63), (1, 0.82), (0.74, 1), (0.26, 1), (0, 0.88)],
        [(0.95, 0.08), (0.58, 0), (0.2, 0.17), (0, 0.48), (0, 0.79), (0.27, 1), (0.73, 1), (1, 0.79),
            (1, 0.62), (0.74, 0.45), (0.27, 0.45), (0, 0.58)],
        [(0, 0), (1, 0), (0.28, 1)],
        [(0.5, 0.47), (0.95, 0.28), (0.95, 0.15), (0.72, 0), (0.28, 0), (0.05, 0.15), (0.05, 0.28),
            (0.5, 0.47), (1, 0.69), (1, 0.83), (0.75, 1), (0.25, 1), (0, 0.83), (0, 0.69), (0.5, 0.47)],
        [(1, 0.42), (0.73, 0.55), (0.26, 0.55), (0, 0.38), (0, 0.2), (0.27, 0), (0.73, 0), (1, 0.2),
            (1, 0.52), (0.8, 0.83), (0.42, 1), (0.05, 0.92)]
    ];

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

        double y = start.Y;
        double arrowHalfHeight = arrowLength * 0.34;
        var tip = new Point(right, y);
        strokes.Add(CreateSpatialShapeStroke(ink,
        [
            new Point(left, y), tip, new Point(right - arrowLength, y - arrowHalfHeight),
            tip, new Point(right - arrowLength, y + arrowHalfHeight)
        ]));

        // Keep zero at the center and leave room for the arrow even when dragged right to left.
        int ticksOnEachSide = Math.Clamp((int)(usableHalfWidth / minimumSpacing), 1, 9);
        double spacing = usableHalfWidth / ticksOnEachSide;
        double origin = left + width / 2;
        double tickHalfHeight = Math.Max(4, inkWidth * 0.4);
        double digitHeight = Math.Clamp(spacing * 0.44, 10, 16);
        var labelInk = ink.Clone();
        labelInk.Width = labelInk.Height = Math.Clamp(inkWidth * 0.45, 0.9, 2.0);

        for (int tick = -ticksOnEachSide; tick <= ticksOnEachSide; tick++)
        {
            double x = origin + tick * spacing;
            double halfHeight = tick == 0 ? tickHalfHeight * 1.45 : tickHalfHeight;
            strokes.Add(CreateSpatialShapeStroke(ink,
                [new Point(x, y - halfHeight), new Point(x, y + halfHeight)]));
            if (showLabels)
                AddNumberLineLabel(strokes, labelInk, tick, x, y + tickHalfHeight * 1.45 + inkWidth / 2 + 4, digitHeight);
        }

        return strokes;
    }

    private static void AddNumberLineLabel(StrokeCollection strokes, DrawingAttributes ink, int value,
        double centerX, double top, double height)
    {
        double digitWidth = height * 0.58;
        double left = centerX - digitWidth / 2;
        if (value < 0)
        {
            double signY = top + height * 0.55;
            double signWidth = digitWidth * 0.55;
            double gap = digitWidth * 0.28;
            left += (signWidth + gap) / 2;
            strokes.Add(CreateSpatialShapeStroke(ink,
                [new Point(left - gap - signWidth, signY), new Point(left - gap, signY)]));
        }

        var digit = NumberLineDigits[Math.Abs(value)];
        var points = new Point[digit.Length];
        for (int i = 0; i < digit.Length; i++)
            points[i] = new Point(left + digit[i].X * digitWidth, top + digit[i].Y * height);
        strokes.Add(CreateSpatialShapeStroke(ink, points));
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
