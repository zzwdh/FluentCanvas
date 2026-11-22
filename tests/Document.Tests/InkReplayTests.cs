using FluentCanvas.Helpers;
using System;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

internal static class InkReplayTests
{
    internal static void Run()
    {
        var original = CreateStroke(5);
        var output = new StrokeCollection();
        var replay = new InkReplaySession(new StrokeCollection { original }, output);

        replay.Advance(TimeSpan.Zero);
        var preview = output[0];
        int updates = 0;
        preview.StylusPointsChanged += (_, _) => updates++;
        replay.Advance(TimeSpan.FromMilliseconds(9));
        if (preview.StylusPoints.Count != 1 || updates != 0)
            throw new InvalidOperationException("Handwritten stroke advanced before its next sample was due.");

        replay.Advance(TimeSpan.FromMilliseconds(10));
        if (preview.StylusPoints.Count != 3 || updates != 1)
            throw new InvalidOperationException("Handwritten samples were not appended in a single batch.");

        replay.Advance(TimeSpan.FromMilliseconds(20));
        if (output.Count != 1 || !ReferenceEquals(preview, output[0]) || preview.StylusPoints.Count != 5 || updates != 2)
            throw new InvalidOperationException("Replay replaced the preview stroke as samples were appended.");

        for (int i = 0; i < original.StylusPoints.Count; i++)
        {
            var actual = preview.StylusPoints[i];
            var expected = original.StylusPoints[i];
            if (actual.X != expected.X || actual.Y != expected.Y || actual.PressureFactor != expected.PressureFactor ||
                actual.GetPropertyValue(StylusPointProperties.XTiltOrientation) !=
                expected.GetPropertyValue(StylusPointProperties.XTiltOrientation))
                throw new InvalidOperationException("Replay changed a position, pressure or tilt reading.");
        }

        if (!preview.DrawingAttributes.IsHighlighter || preview.DrawingAttributes.Color != original.DrawingAttributes.Color ||
            replay.Advance(TimeSpan.FromMilliseconds(119)) || !replay.Advance(TimeSpan.FromMilliseconds(120)))
            throw new InvalidOperationException("Replay dropped the original ink style or finished before the final hold elapsed.");

        var closedShapePreview = new StrokeCollection();
        var closedShapeReplay = new InkReplaySession(new StrokeCollection { CreateStroke(629), original }, closedShapePreview);
        closedShapeReplay.Advance(TimeSpan.Zero);
        if (closedShapePreview[0].StylusPoints.Count != 50)
            throw new InvalidOperationException("The circle and ellipse replay cadence changed.");

        closedShapeReplay.Advance(TimeSpan.FromMilliseconds(129));
        if (closedShapePreview.Count != 1 || closedShapePreview[0].StylusPoints.Count != 629)
            throw new InvalidOperationException("Replay advanced to the next stroke before the first stroke was finished.");

        closedShapeReplay.Advance(TimeSpan.FromMilliseconds(130));
        if (closedShapePreview.Count != 2 || closedShapePreview[1].StylusPoints.Count != 2)
            throw new InvalidOperationException("Replay did not preserve timing between a shape and a handwritten stroke.");
    }

    private static Stroke CreateStroke(int count)
    {
        var description = new StylusPointDescription([
            new StylusPointPropertyInfo(StylusPointProperties.X),
            new StylusPointPropertyInfo(StylusPointProperties.Y),
            new StylusPointPropertyInfo(StylusPointProperties.NormalPressure),
            new StylusPointPropertyInfo(StylusPointProperties.XTiltOrientation),
        ]);
        var samples = new StylusPointCollection(description, count);
        for (int i = 0; i < count; i++)
            samples.Add(new StylusPoint(i, 2 * i, 0.1f + i % 5 * 0.15f, description, [i % 30]));
        return new Stroke(samples, new DrawingAttributes { Color = Colors.Blue, IsHighlighter = true });
    }
}
