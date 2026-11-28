using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

// Independent reader for the geometry and brush fields checked by the Windows PowerShell 5.1 UI harness.
public static class NativeInkEvidence
{
    private const int MaxPayload = 256 * 1024 * 1024;
    public const int MaxFileBytes = MaxPayload + 48 + 65536;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FCINK\r\n\u001a");

    public static StrokeCollection Read(Stream input)
    {
        using (var reader = new BinaryReader(input, Encoding.UTF8, true))
        {
            if (!reader.ReadBytes(8).SequenceEqual(Magic))
                throw new InvalidDataException("Unrecognized native ink document.");
            uint version = reader.ReadUInt32();
            if (version != 1 && version != 2) throw new InvalidDataException("Unknown native ink version.");
            int size = reader.ReadInt32();
            if (size < 0 || size > MaxPayload) throw new InvalidDataException("Invalid native ink length.");
            byte[] payload = version == 1 ? reader.ReadBytes(size) : ReadBlocks(reader, size);
            using (var hash = SHA256.Create())
            {
                if (payload.Length != size || !hash.ComputeHash(payload).SequenceEqual(reader.ReadBytes(32)) || input.ReadByte() != -1)
                    throw new InvalidDataException("Native ink length or digest mismatch.");
            }
            var result = new StrokeCollection();
            var ids = new HashSet<ulong>();
            using (var bytes = new MemoryStream(payload, false))
            using (var data = new BinaryReader(bytes))
            {
                while (bytes.Position < bytes.Length)
                {
                    ulong id = data.ReadUInt64();
                    int count = data.ReadInt32(), styleSize = data.ReadInt32();
                    long end = bytes.Position + count * 20L + styleSize;
                    if (id == 0 || !ids.Add(id) || count < 1 || styleSize < 27 || end > bytes.Length)
                        throw new InvalidDataException("Invalid stroke record.");
                    var points = new StylusPointCollection();
                    for (int i = 0; i < count; i++)
                        points.Add(new StylusPoint(data.ReadDouble(), data.ReadDouble(), data.ReadSingle()));
                    if (data.ReadByte() != 1) throw new InvalidDataException("Unsupported brush version.");
                    var brush = new DrawingAttributes();
                    brush.Color = Color.FromArgb(data.ReadByte(), data.ReadByte(), data.ReadByte(), data.ReadByte());
                    brush.Width = data.ReadDouble(); brush.Height = data.ReadDouble();
                    brush.StylusTip = (StylusTip)data.ReadByte();
                    brush.FitToCurve = data.ReadBoolean(); brush.IgnorePressure = data.ReadBoolean(); brush.IsHighlighter = data.ReadBoolean();
                    if (data.ReadBoolean())
                        brush.StylusTipTransform = new Matrix(data.ReadDouble(), data.ReadDouble(), data.ReadDouble(), data.ReadDouble(), data.ReadDouble(), data.ReadDouble());
                    data.ReadByte(); // Packet and replay flags lie outside the geometry hash.
                    if (bytes.Position > end) throw new InvalidDataException("Truncated brush.");
                    bytes.Position = end;
                    result.Add(new Stroke(points, brush));
                }
            }
            return result;
        }
    }

    private static byte[] Exact(BinaryReader reader, int size)
    {
        byte[] bytes = reader.ReadBytes(size);
        if (bytes.Length != size) throw new EndOfStreamException();
        return bytes;
    }

    private static byte[] ReadBlocks(BinaryReader file, int size)
    {
        var result = new byte[size];
        int blocks = 0;
        for (int offset = 0; offset < size;)
        {
            int n = file.ReadInt32(), schema = file.ReadInt32(), stored = file.ReadInt32(), mode = file.ReadInt32();
            if (++blocks > 4096 || n <= 0 || n > 262144 || n > size - offset || stored <= 0 || stored > n ||
                schema <= 0 || schema > 524288 || mode < 0 || mode > 2 || (mode != 2 && schema != n) ||
                (mode == 0 && stored != n)) throw new InvalidDataException("Invalid ink block.");
            byte[] bytes = Exact(file, stored);
            if (mode != 0)
            {
                using (var source = new MemoryStream(bytes, false))
                using (var inflate = new DeflateStream(source, CompressionMode.Decompress))
                using (var reader = new BinaryReader(inflate))
                {
                    bytes = Exact(reader, schema);
                    if (inflate.ReadByte() != -1) throw new InvalidDataException("Excess block data.");
                }
            }
            if (mode != 2) Buffer.BlockCopy(bytes, 0, result, offset, n);
            else DecodePlanes(bytes, result, offset, n);
            offset += n;
        }
        return result;
    }

    private static void DecodePlanes(byte[] bytes, byte[] result, int offset, int size)
    {
        using (var source = new MemoryStream(bytes, false))
        using (var reader = new BinaryReader(source))
        using (var output = new MemoryStream(result, offset, size, true))
        using (var writer = new BinaryWriter(output))
        {
            var brushes = new List<byte[]>();
            int format = reader.ReadInt32();
            bool decimalCoordinates = format < 0;
            int brushCount = format & Int32.MaxValue;
            for (int i = 0; i < brushCount; i++)
            {
                int length = reader.ReadInt32();
                if (length < 0 || length > bytes.Length - source.Position) throw new InvalidDataException();
                brushes.Add(Exact(reader, length));
            }
            var layout = new List<Tuple<ulong,int,byte[],byte[]>>();
            int count = reader.ReadInt32(), samples = 0;
            int remaining = size;
            for (int i = 0; i < count; i++)
            {
                ulong id = reader.ReadUInt64();
                int points = reader.ReadInt32(), style = reader.ReadInt32(), tailSize = reader.ReadInt32();
                if (style < 0 || style >= brushes.Count || tailSize < 0 || tailSize > remaining ||
                    points <= 0 || points > remaining / 20) throw new InvalidDataException();
                remaining -= 16 + points * 20 + brushes[style].Length + tailSize;
                if (remaining < 0) throw new InvalidDataException();
                samples += points;
                layout.Add(Tuple.Create(id, points, brushes[style], Exact(reader, tailSize)));
            }
            int firstLane = decimalCoordinates ? 16 : 0;
            long planeBytes = samples * (20 - firstLane);
            if (remaining != 0 || bytes.Length - source.Position < planeBytes) throw new InvalidDataException();
            int pos = (int)source.Position;
            source.Position += planeBytes;
            foreach (var row in layout)
            {
                writer.Write(row.Item1); writer.Write(row.Item2); writer.Write(row.Item3.Length + row.Item4.Length);
                var previous = new byte[20];
                long previousX = 0, previousY = 0;
                for (int i = 0; i < row.Item2; i++, pos++)
                {
                    if (decimalCoordinates)
                    {
                        writer.Write(ReadAxis(reader, ref previousX)); writer.Write(ReadAxis(reader, ref previousY));
                    }
                    for (int lane = firstLane; lane < 20; lane++)
                    {
                        previous[lane] ^= bytes[pos + (lane - firstLane) * samples];
                        writer.Write(previous[lane]);
                    }
                }
                writer.Write(row.Item3); writer.Write(row.Item4);
            }
            if (source.Position != source.Length) throw new InvalidDataException();
        }
    }

    private static double ReadAxis(BinaryReader reader, ref long previous)
    {
        long code = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            int b = reader.ReadByte();
            code |= (long)(b & 127) << shift;
            if (b < 128)
            {
                if (code == 0) return reader.ReadDouble();
                long zig = code - 1;
                previous += (zig >> 1) ^ -(zig & 1);
                if (Math.Abs(previous) > 1000000000L) throw new InvalidDataException();
                return previous / 100.0;
            }
        }
        throw new InvalidDataException();
    }

    // The sandbox fixture contains ordinary XY/pressure strokes with the default packet description.
    public static void WriteFixture(Stream output, StrokeCollection strokes)
    {
        using (var payload = new MemoryStream())
        using (var data = new BinaryWriter(payload, Encoding.UTF8, true))
        {
            ulong id = 0;
            foreach (var stroke in strokes)
            {
                var brush = stroke.DrawingAttributes;
                if (!brush.StylusTipTransform.IsIdentity) throw new ArgumentException("Fixture expects identity brush transforms.");
                data.Write(++id); data.Write(stroke.StylusPoints.Count); data.Write(27);
                foreach (var point in stroke.StylusPoints)
                {
                    data.Write(point.X); data.Write(point.Y); data.Write(point.PressureFactor);
                }
                data.Write((byte)1);
                data.Write(brush.Color.A); data.Write(brush.Color.R); data.Write(brush.Color.G); data.Write(brush.Color.B);
                data.Write(brush.Width); data.Write(brush.Height); data.Write((byte)brush.StylusTip);
                data.Write(brush.FitToCurve); data.Write(brush.IgnorePressure); data.Write(brush.IsHighlighter);
                data.Write(false); data.Write(false);
            }
            data.Flush();
            using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
            using (var hash = SHA256.Create())
            {
                writer.Write(Magic); writer.Write(1U); writer.Write(checked((int)payload.Length));
                payload.Position = 0; payload.CopyTo(output);
                payload.Position = 0; writer.Write(hash.ComputeHash(payload));
            }
        }
    }
}
