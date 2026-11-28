using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentCanvas.Helpers;

internal enum GeometryKind : byte { Arc = 1, Triangle, Axis, Sector, Chord, NumberLine }

internal sealed record ParametricInk(GeometryKind Kind, Point A, Point B, Point C,
    double Parameter = 0, double OriginalWidth = 0, int Part = 0, int Ticks = 0, Guid Group = default)
{
    internal const int DescriptorSize = 41;
    internal const double Tolerance = 0.25;
    internal static readonly Guid GeometryProperty = new("61C46A90-1ADC-41C7-8734-A4A5115C93CA");

    internal static ParametricInk Ellipse(Point start, Point end)
    {
        var center = start + (end - start) / 2;
        return new(GeometryKind.Arc, center, new Point(end.X, center.Y), new Point(center.X, end.Y), Math.Tau);
    }

    internal static ParametricInk Arrow(Point start, Point end)
    {
        Vector direction = end - start;
        double length = Math.Max(0.01, direction.Length);
        Point across = start + new Vector(-direction.Y, direction.X) * (7 / length);
        return new(GeometryKind.Axis, start, end, across, 20 / length);
    }

    internal byte[] Encode()
    {
        byte[] data = new byte[DescriptorSize + 48];
        data[0] = (byte)Kind;
        Double(1, Parameter);
        Double(9, OriginalWidth);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(17), Part);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(21), Ticks);
        Group.TryWriteBytes(data.AsSpan(25,16));
        Double(41, A.X); Double(49, A.Y); Double(57, B.X); Double(65, B.Y); Double(73, C.X); Double(81, C.Y);
        return data;

        void Double(int offset, double value) => BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(offset), value);
    }

    private static ParametricInk Decode(byte[] data)
    {
        if (data.Length != DescriptorSize + 48) throw new InvalidDataException("Invalid shape payload.");
        double Read(int offset) => BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(offset));
        return new((GeometryKind)data[0], new(Read(41),Read(49)), new(Read(57),Read(65)), new(Read(73),Read(81)),
            Read(1), Read(9), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(17)),
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(21)), new Guid(data.AsSpan(25, 16)));
    }

    internal static ParametricInk FromDescriptor(byte[] descriptor, StylusPointCollection controls)
    {
        if (descriptor.Length != DescriptorSize || controls.Count != 3) throw new InvalidDataException("Invalid shape controls.");
        return new((GeometryKind)descriptor[0], controls[0].ToPoint(), controls[1].ToPoint(), controls[2].ToPoint(),
            BinaryPrimitives.ReadDoubleLittleEndian(descriptor.AsSpan(1)), BinaryPrimitives.ReadDoubleLittleEndian(descriptor.AsSpan(9)),
            BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(17)), BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(21)),
            new Guid(descriptor.AsSpan(25, 16)));
    }

    internal static ParametricInk Get(Stroke stroke) =>
        stroke.ContainsPropertyData(GeometryProperty) && stroke.GetPropertyData(GeometryProperty) is byte[] data
            ? Decode(data) : null;

    internal StylusPointCollection Controls => new(new[] { A, B, C });

    internal StylusPointCollection Project(double tolerance = Tolerance)
    {
        byte[] data = Encode();
        double[] xy = ArrayPool<double>.Shared.Rent(128);
        try
        {
            int status = ProjectTo(xy, out nuint count);
            if (status == 3 && count <= 16400)
            {
                var larger = ArrayPool<double>.Shared.Rent(checked((int)count));
                ArrayPool<double>.Shared.Return(xy);
                xy = larger;
                status = ProjectTo(xy, out count);
            }
            if (status != 0 || count < 4 || count > (nuint)xy.Length || count % 2 != 0)
                throw new InvalidDataException("Invalid teaching geometry.");

            var points = new StylusPointCollection((int)count / 2);
            for (int i = 0; i < (int)count; i += 2) points.Add(new StylusPoint(xy[i], xy[i + 1]));
            return points;
        }
        finally { ArrayPool<double>.Shared.Return(xy); }

        int ProjectTo(double[] buffer, out nuint count) =>
            fc_shape_project(data, (nuint)data.Length, tolerance, buffer, (nuint)buffer.Length, out count);
    }

    internal Stroke Create(DrawingAttributes brush)
    {
        var style = brush.Clone();
        style.FitToCurve = false;
        var stroke = new Stroke(Project(), style);
        stroke.AddPropertyData(GeometryProperty, Encode());
        stroke.AddPropertyData(NativeStrokeCodec.ShapeProperty, true);
        return stroke;
    }

    internal void Apply(Stroke stroke)
    {
        var points = Project();
        stroke.AddPropertyData(GeometryProperty, Encode());
        stroke.StylusPoints = points;
    }

    internal static void CopyGeometry(Stroke target, Stroke source)
    {
        if (source.ContainsPropertyData(GeometryProperty))
            target.AddPropertyData(GeometryProperty, source.GetPropertyData(GeometryProperty));
        else if (target.ContainsPropertyData(GeometryProperty)) target.RemovePropertyData(GeometryProperty);
    }

    internal ParametricInk Transformed(Matrix matrix) => this with
        { A = matrix.Transform(A), B = matrix.Transform(B), C = matrix.Transform(C) };

    internal IEnumerable<Point> SnapAnchors()
    {
        for (int index = 0; index < HandleCount; index++) yield return Handle(index);
        if (Kind == GeometryKind.Arc && Math.Abs(Parameter) >= Math.Tau - 1e-9)
        {
            yield return A - (B - A);
            yield return A - (C - A);
        }
    }

    internal int HandleCount => Kind is GeometryKind.Arc or GeometryKind.Sector or GeometryKind.Chord &&
        Math.Abs(Parameter) < Math.Tau - 1e-9 ? 4 : Kind == GeometryKind.NumberLine ? 2 : 3;

    internal Point Handle(int index) => index switch
    {
        0 => A, 1 => B, 2 => C,
        _ => A + (B-A)*Math.Cos(Parameter) + (C-A)*Math.Sin(Parameter)
    };

    internal ParametricInk WithHandle(int index, Point target)
    {
        if (index == 3)
        {
            var u=B-A; var v=C-A; var ray=target-A;
            double det=Vector.CrossProduct(u,v);
            if (Math.Abs(det)<1e-6) return this;
            double angle=Math.Atan2(Vector.CrossProduct(u,ray)/det,Vector.CrossProduct(ray,v)/det);
            if (Parameter>0 && angle<0) angle+=Math.Tau;
            if (Parameter<0 && angle>0) angle-=Math.Tau;
            return this with {Parameter=angle};
        }
        if (Kind==GeometryKind.NumberLine || Kind==GeometryKind.Axis)
        {
            if (index==0 || index==1)
            {
                Point fixedPoint=index==1 ? A : B;
                Vector current=(index==1 ? B : A)-fixedPoint;
                Vector next=target-fixedPoint;
                if (current.LengthSquared < 1e-6 || next.Length<2) return this;
                double x=Vector.Multiply(current,next)/current.LengthSquared;
                double y=Vector.CrossProduct(current,next)/current.LengthSquared;
                Matrix m=new(x,y,-y,x,0,0);
                var shift=fixedPoint-m.Transform(fixedPoint);
                m.Translate(shift.X,shift.Y);
                return Transformed(m);
            }
        }
        if (index==0 && Kind!=GeometryKind.Triangle)
            return this with { A=target, B=B+(target-A), C=C+(target-A) };
        return index switch {0 => this with {A=target}, 1 => this with {B=target}, _=>this with {C=target}};
    }

    [DllImport("fluent_canvas_core", CallingConvention=CallingConvention.Cdecl)]
    private static extern int fc_shape_project(byte[] descriptor, nuint length, double tolerance,
        [Out] double[] xy, nuint capacity, out nuint count);
}

internal sealed class ShapeAnchorIndex
{
    private const int Radius = 8;
    private readonly Dictionary<(int X, int Y), HashSet<Point>> cells = new();
    private static int Cell(double value) => (int)Math.Floor(value / Radius);

    internal void Add(Point point)
    {
        var key = (Cell(point.X), Cell(point.Y));
        if (!cells.TryGetValue(key, out var points)) cells[key] = points = new();
        points.Add(point);
    }

    internal Point Snap(Point position)
    {
        var center = (X: Cell(position.X), Y: Cell(position.Y));
        double distance = Radius;
        Point nearest = position;
        for (int x = center.X - 1; x <= center.X + 1; x++)
            for (int y = center.Y - 1; y <= center.Y + 1; y++)
                if (cells.TryGetValue((x, y), out var points))
                    foreach (var point in points)
                    {
                        double candidate = (point - position).Length;
                        if (candidate >= distance) continue;
                        nearest = point;
                        distance = candidate;
                    }
        return nearest;
    }
}

