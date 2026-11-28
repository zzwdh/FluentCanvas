using FluentCanvas.Helpers;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class ParametricInkTests
{
    internal static void Verify(string root)
    {
        var ellipse = ParametricInk.Ellipse(new Point(10,20), new Point(110,80));
        var arc = new ParametricInk(GeometryKind.Arc, new(100,100), new(140,100), new(100,140), Math.PI / 3);
        var triangle = new ParametricInk(GeometryKind.Triangle,new(0,0),new(50,0),new(25,50));
        var axis = ParametricInk.Arrow(new(0,50),new(200,50));
        var line = new ParametricInk(GeometryKind.NumberLine, new(0,60), new(300,60), new(0,360),
            3, 300, 0, 4, Guid.NewGuid());
        var shapes = new[] { ellipse, arc, triangle, axis, line, line with { Part=6 }, line with { Part=26 }, line with { Part=46 } };
        var brush = new DrawingAttributes { Color=Colors.DarkGreen, Width=4, Height=4 };
        var canvas = new InkCanvas();
        foreach (var shape in shapes)
        {
            var stroke = shape.Create(brush);
            canvas.Strokes.Add(stroke);
            using var raw = new MemoryStream();
            using var writer = new BinaryWriter(raw);
            NativeStrokeCodec.Write(writer, 7, stroke);
            byte[] record = raw.ToArray();
            Require(BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8)) == 3, "shape was serialized as a polyline");
            var reopened = NativeStrokeCodec.Decode(record);
            Require(shape == ParametricInk.Get(reopened) && stroke.StylusPoints.SequenceEqual(reopened.StylusPoints),
                "native shape metadata or projection changed");
        }
        var history = new TimeMachine();
        history.BindInk(canvas.Strokes);
        history.CommitStrokeUserInputHistory(new StrokeCollection(canvas.Strokes));
        string file = Path.Combine(root, "parameter-shapes.icart");
        using (var snapshot = CanvasDocument.Capture(history.FreezeInk(), [])) snapshot.Save(file);
        var restored = new InkCanvas();
        var restoredHistory = new TimeMachine();
        CanvasDocument.LoadInto(file, restored, restoredHistory);
        Require(restored.Strokes.Select(ParametricInk.Get).SequenceEqual(shapes), "document changed controls");
        var tip = restored.Strokes[0];
        var edited = ellipse.WithHandle(1,new(185,50));
        edited.Apply(tip);
        restoredHistory.CommitStrokeManipulationHistory([tip],null);
        Require(ParametricInk.Get(restoredHistory.MaterializeInk()[0]) == edited, "resized ellipse was not committed");
        restoredHistory.Undo();
        Require(ParametricInk.Get(restoredHistory.MaterializeInk()[0]) == ellipse, "undo lost ellipse controls");
        var adjusted = arc.WithHandle(3,new(100,180));
        Require(Math.Abs(adjusted.Parameter - Math.PI / 2)<1e-10,"arc end did not change sweep");
        Require(ellipse.Project(0.05).Count>ellipse.Project(0.5).Count,"projection did not respond to pixel tolerance");
        Require(triangle.WithHandle(2,new(25,65)).Project()[2].Y == 65, "triangle vertex edit was lost");
        var anchors = new ShapeAnchorIndex();
        foreach (var vertex in triangle.SnapAnchors()) anchors.Add(vertex);
        var snapped = anchors.Snap(new(51,1));
        Require(snapped == triangle.B,"nearby triangle vertex did not snap");
        Require(anchors.Snap(new(63,1)) == new Point(63,1),"distant point snapped");

        restored.Select(new StrokeCollection {tip});
        var render = CanvasSelectionImage.Render(restored);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(render));
        using var png = new MemoryStream();
        encoder.Save(png);
        png.Position=0;
        var pixels = new FormatConvertedBitmap(BitmapFrame.Create(png), PixelFormats.Bgra32,null,0);
        int stride=pixels.PixelWidth*4;
        var data=new byte[pixels.PixelHeight*stride];
        pixels.CopyPixels(data,stride,0);
        int centerY=pixels.PixelHeight/2;
        Require(data[centerY*stride+(pixels.PixelWidth/2)*4+3]==0 &&
            Enumerable.Range(0,pixels.PixelWidth/4).Any(x=>data[centerY*stride+x*4+3]>0), "exported ellipse has incorrect fill or outline");
    }

    private static void Require(bool value,string message)
    {
        if (!value) throw new InvalidDataException(message);
    }
}
