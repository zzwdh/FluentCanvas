using System;
using System.IO;
using System.Linq;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using FluentCanvas.Helpers;

internal static class NativeStrokeCodecTests
{
    internal static void Run()
    {
        var standard = new Stroke(new StylusPointCollection { new StylusPoint(0.125, -3.75, 0.37f) });
        var description = new StylusPointDescription([
            new StylusPointPropertyInfo(StylusPointProperties.X),
            new StylusPointPropertyInfo(StylusPointProperties.Y),
            new StylusPointPropertyInfo(StylusPointProperties.NormalPressure),
            new StylusPointPropertyInfo(StylusPointProperties.XTiltOrientation),
            new StylusPointPropertyInfo(StylusPointProperties.BarrelButton)
        ]);
        var points = new StylusPointCollection(description);
        points.Add(new StylusPoint(1.125, 2.4567, 0.21f, description, [17, 1]));
        points.Add(new StylusPoint(2.087, 4.75, 0.85f, description, [37, 0]));
        var extended = new Stroke(points, new DrawingAttributes
        {
            Width = 3.125, Height = 5.375, Color = Colors.Orchid, IsHighlighter = true, FitToCurve = true,
            StylusTip = StylusTip.Rectangle, StylusTipTransform = new Matrix(1, 0.25, 0.5, 1, 0, 0)
        });
        var metadata = Guid.NewGuid();
        extended.AddPropertyData(metadata, "annotation");
        extended.DrawingAttributes.AddPropertyData(metadata, 117);
        foreach (var source in new[] { standard, extended })
        {
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            NativeStrokeCodec.Write(writer, 17, source);
            var decoded = NativeStrokeCodec.Decode(output.ToArray());
            if (!decoded.StylusPoints.SequenceEqual(source.StylusPoints) ||
                !decoded.DrawingAttributes.Equals(source.DrawingAttributes) ||
                decoded.ContainsPropertyData(metadata) != source.ContainsPropertyData(metadata) ||
                (source.ContainsPropertyData(metadata) && !Equals(decoded.GetPropertyData(metadata), "annotation")))
                throw new InvalidDataException("The native page changed the stroke samples or rendering data.");
        }
    }
}
