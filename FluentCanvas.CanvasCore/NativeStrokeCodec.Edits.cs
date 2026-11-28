using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Ink;

namespace FluentCanvas.Helpers;

internal static partial class NativeStrokeCodec
{
    // A custom template can contain historical drawing properties. Re-encode
    // such strokes on brush edits so removing the last property stays lossless.
    private static readonly ConditionalWeakTable<Stroke, object> templateStrokes = new();

    internal static bool WriteBrushEdit(BinaryWriter writer, ulong id, Stroke stroke)
    {
        if (templateStrokes.TryGetValue(stroke, out _) || stroke.GetPropertyDataIds().Any(id => id != ShapeProperty && id != ParametricInk.GeometryProperty) ||
            stroke.DrawingAttributes.GetPropertyDataIds().Any(IsCustomAttribute)) return false;
        writer.Write(id);
        writer.Write(stroke.DrawingAttributes.StylusTipTransform.IsIdentity ? 26 : 74);
        writer.Write((byte)1);
        WriteBrush(writer, stroke.DrawingAttributes);
        return true;
    }
}
