using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentCanvas.Helpers;

// The core reads ordered (x, y, pressure) samples directly. Its opaque rendering
// payload carries the brush and Windows pen packet channels used by the WPF view.
internal static partial class NativeStrokeCodec
{
    private const int HeaderSize = 16, SampleSize = 20;
    internal static readonly Guid ShapeProperty = new("151b57f4-6037-44ae-bb66-f3973745b80c");
    private static readonly StylusPointDescription DefaultDescription = new();

    internal static void Write(BinaryWriter writer, ulong id, Stroke stroke)
    {
        var geometry = ParametricInk.Get(stroke);
        var points = geometry?.Controls ?? stroke.StylusPoints;
        writer.Write(id);
        writer.Write(points.Count);
        long lengthPosition = writer.BaseStream.Position;
        writer.Write(0);
        foreach (var point in points)
        {
            writer.Write(point.X);
            writer.Write(point.Y);
            writer.Write(point.PressureFactor);
        }

        long start = writer.BaseStream.Position;
        writer.Write((byte)1);
        WriteBrush(writer, stroke.DrawingAttributes);
        var properties = points.Description.GetStylusPointProperties();
        bool hasPacketDescription = !HasDefaultDescription(points.Description);
        byte flags = (byte)((hasPacketDescription ? 1 : 0) | (stroke.ContainsPropertyData(ShapeProperty) ? 2 : 0) | (geometry != null ? 4 : 0));
        writer.Write(flags);
        if (geometry != null) writer.Write(geometry.Encode(), 0, ParametricInk.DescriptorSize);
        if (hasPacketDescription)
        {
            writer.Write(properties.Count);
            foreach (var property in properties)
            {
                writer.Write(property.Id.ToByteArray());
                writer.Write(property.IsButton);
                writer.Write(property.Minimum);
                writer.Write(property.Maximum);
                writer.Write((int)property.Unit);
                writer.Write(property.Resolution);
            }
            foreach (var point in points)
                for (int i = 3; i < properties.Count; i++)
                    writer.Write(point.GetPropertyValue(properties[i]));
        }

        // WPF supports arbitrary application-defined metadata. Only these strokes
        // need an ISF template. All point samples and standard brush fields are exact.
        var custom = stroke.GetPropertyDataIds().Where(id => id != ShapeProperty && id != ParametricInk.GeometryProperty).ToArray();
        if (custom.Length > 0 || stroke.DrawingAttributes.GetPropertyDataIds().Any(IsCustomAttribute))
        {
            templateStrokes.GetValue(stroke, _ => new object());
            var template = new Stroke(new StylusPointCollection { new StylusPoint(0, 0) }, stroke.DrawingAttributes);
            foreach (Guid property in custom)
                template.AddPropertyData(property, stroke.GetPropertyData(property));
            new StrokeCollection { template }.Save(writer.BaseStream);
        }
        long end = writer.BaseStream.Position;
        writer.BaseStream.Position = lengthPosition;
        writer.Write(checked((int)(end - start)));
        writer.BaseStream.Position = end;
    }

    private static bool HasDefaultDescription(StylusPointDescription description)
    {
        if (description.PropertyCount != 3) return false;
        foreach (var property in DefaultDescription.GetStylusPointProperties())
        {
            var actual = description.GetPropertyInfo(property);
            if (actual.Minimum != property.Minimum || actual.Maximum != property.Maximum ||
                actual.Resolution != property.Resolution || actual.Unit != property.Unit) return false;
        }
        return true;
    }

    private static bool IsCustomAttribute(Guid id) =>
        id != DrawingAttributeIds.Color && id != DrawingAttributeIds.DrawingFlags &&
        id != DrawingAttributeIds.IsHighlighter && id != DrawingAttributeIds.StylusHeight &&
        id != DrawingAttributeIds.StylusWidth && id != DrawingAttributeIds.StylusTip &&
        id != DrawingAttributeIds.StylusTipTransform;

    private static void WriteBrush(BinaryWriter writer, DrawingAttributes brush)
    {
        var color = brush.Color;
        writer.Write(color.A);
        writer.Write(color.R);
        writer.Write(color.G);
        writer.Write(color.B);
        writer.Write(brush.Width);
        writer.Write(brush.Height);
        writer.Write((byte)brush.StylusTip);
        writer.Write(brush.FitToCurve);
        writer.Write(brush.IgnorePressure);
        writer.Write(brush.IsHighlighter);
        var matrix = brush.StylusTipTransform;
        writer.Write(!matrix.IsIdentity);
        if (matrix.IsIdentity) return;
        writer.Write(matrix.M11);
        writer.Write(matrix.M12);
        writer.Write(matrix.M21);
        writer.Write(matrix.M22);
        writer.Write(matrix.OffsetX);
        writer.Write(matrix.OffsetY);
    }

    internal static StrokeCollection DecodePage(byte[] records, Action<Stroke, ulong> bind = null)
    {
        var strokes = new StrokeCollection();
        int offset = 0;
        while (offset < records.Length)
        {
            int size = RecordSize(records.AsSpan(offset));
            var stroke = Decode(records, offset, size);
            bind?.Invoke(stroke, BinaryPrimitives.ReadUInt64LittleEndian(records.AsSpan(offset)));
            strokes.Add(stroke);
            offset += size;
        }
        return strokes;
    }

    internal static int RecordSize(ReadOnlySpan<byte> record)
    {
        if (record.Length < HeaderSize) throw new InvalidDataException("Truncated ink record.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(record[12..]);
        long size = HeaderSize + (long)SampleSize * count + length;
        if (count < 1 || length < 1 || size > record.Length)
            throw new InvalidDataException("Invalid ink record length.");
        return (int)size;
    }

    internal static Stroke Decode(byte[] record)
    {
        if (RecordSize(record) != record.Length) throw new InvalidDataException("Unexpected trailing ink data.");
        return Decode(record, 0, record.Length);
    }

    private static Stroke Decode(byte[] record, int offset, int size)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(offset + 8));
        int length = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(offset + 12));
        using var stream = new MemoryStream(record, offset + size - length, length, writable: false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadByte() != 1) throw new InvalidDataException("Unsupported Windows ink payload.");
        var brush = ReadBrush(reader);
        var description = DefaultDescription;
        byte flags = reader.ReadByte();
        if ((flags & ~7) != 0 || (flags & 5) == 5) throw new InvalidDataException("Invalid ink record flags.");
        byte[] shape = (flags & 4) != 0 ? reader.ReadBytes(ParametricInk.DescriptorSize) : null;
        if ((flags & 1) != 0)
        {
            int propertyCount = reader.ReadInt32();
            if (propertyCount < 3 || (long)propertyCount * 33 > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid pen packet description.");
            var properties = new StylusPointPropertyInfo[propertyCount];
            for (int i = 0; i < properties.Length; ++i)
            {
                var property = new StylusPointProperty(new Guid(reader.ReadBytes(16)), reader.ReadBoolean());
                properties[i] = new StylusPointPropertyInfo(property, reader.ReadInt32(), reader.ReadInt32(),
                    (StylusPointPropertyUnit)reader.ReadInt32(), reader.ReadSingle());
            }
            description = new StylusPointDescription(properties);
        }
        var points = new StylusPointCollection(description, count);
        int extra = description.PropertyCount - 3;
        if ((long)count * extra * sizeof(int) > stream.Length - stream.Position)
            throw new InvalidDataException("Truncated pen packet data.");
        var additional = new int[extra];
        for (int i = 0; i < count; i++)
        {
            var sample = record.AsSpan(offset + HeaderSize + i * SampleSize, SampleSize);
            for (int j = 0; j < additional.Length; j++) additional[j] = reader.ReadInt32();
            points.Add(new StylusPoint(BinaryPrimitives.ReadDoubleLittleEndian(sample),
                BinaryPrimitives.ReadDoubleLittleEndian(sample[8..]), BinaryPrimitives.ReadSingleLittleEndian(sample[16..]),
                description, additional));
        }
        var geometry = shape == null ? null : ParametricInk.FromDescriptor(shape, points);
        var stroke = new Stroke(geometry?.Project() ?? points, brush);
        if (geometry != null) stroke.AddPropertyData(ParametricInk.GeometryProperty, geometry.Encode());
        if (stream.Position < stream.Length)
        {
            var templates = new StrokeCollection(stream);
            if (templates.Count != 1) throw new InvalidDataException("Invalid ink metadata template.");
            var template = templates[0];
            templateStrokes.GetValue(stroke, _ => new object());
            foreach (Guid id in template.DrawingAttributes.GetPropertyDataIds().Where(IsCustomAttribute))
                brush.AddPropertyData(id, template.DrawingAttributes.GetPropertyData(id));
            foreach (Guid id in template.GetPropertyDataIds())
                stroke.AddPropertyData(id, template.GetPropertyData(id));
        }
        if ((flags & 2) != 0) stroke.AddPropertyData(ShapeProperty, true);
        return stroke;
    }
    internal static DrawingAttributes ReadBrush(BinaryReader reader)
    {
        var brush = new DrawingAttributes
        {
            Color = Color.FromArgb(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()),
            Width = reader.ReadDouble(), Height = reader.ReadDouble(), StylusTip = (StylusTip)reader.ReadByte(),
            FitToCurve = reader.ReadBoolean(), IgnorePressure = reader.ReadBoolean(), IsHighlighter = reader.ReadBoolean()
        };
        if (reader.ReadBoolean())
            brush.StylusTipTransform = new Matrix(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(),
                reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        return brush;
    }
}
