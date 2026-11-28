using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace FluentCanvas.Helpers
{
    // Only the InkCanvas data emitted by the original serializer is interpreted. Nothing in
    // the archive is passed to the WPF XAML object loader, a URI resolver or a type converter.
    internal static class CanvasDocumentXml
    {
        internal static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private const string Layout = "Name Width Height InkCanvas.Left InkCanvas.Top InkCanvas.Right InkCanvas.Bottom Stretch Opacity RenderTransform RenderTransformOrigin Source ObjectId Panel.ZIndex";

        internal sealed class Element
        {
            internal Guid Id;
            internal int Z;
            internal bool IsMedia;
            internal string Asset, Name;
            internal double Width, Height, Left, Top, Right, Bottom, Opacity;
            internal Point Origin;
            internal Matrix Transform;
            internal Stretch Stretch;

            internal void Apply(FrameworkElement target)
            {
                SceneObject.Bind(target, Id);
                Panel.SetZIndex(target,Z);
                target.Name = Name;
                target.Width = Width;
                target.Height = Height;
                target.Opacity = Opacity;
                target.RenderTransformOrigin = Origin;
                var group = new TransformGroup();
                group.Children.Add(new MatrixTransform(Transform));
                target.RenderTransform = group;
                InkCanvas.SetLeft(target, Left);
                InkCanvas.SetRight(target, Right);
                InkCanvas.SetTop(target, Top);
                InkCanvas.SetBottom(target, Bottom);
            }
        }

        internal static List<Element> Read(Stream stream) => Read(stream, out _);

        internal static List<Element> Read(Stream stream, out Guid? pageId)
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = CanvasDocument.MaxXmlBytes
            });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != Presentation + "InkCanvas") throw Invalid();
            ValidateNode(root, "Name Background Strokes EditingMode EditingModeInverted MoveEnabled ResizeEnabled Cursor ForceCursor IsManipulationEnabled IsHitTestVisible Opacity Visibility PageId");
            pageId = ReadId(root,"PageId");
            var result = new List<Element>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var objectIds = new HashSet<Guid>();
            foreach (var node in root.Elements())
            {
                if (node.Name == Presentation + "InkCanvas.DefaultDrawingAttributes")
                {
                    ValidateNode(node, "");
                    var drawing = OnlyChild(node, "DrawingAttributes");
                    ValidateLeaf(drawing, "Color StylusTip StylusTipTransform Height Width FitToCurve IgnorePressure IsHighlighter");
                    continue;
                }
                bool media = node.Name == Presentation + "MediaElement";
                if (!media && node.Name != Presentation + "Image") throw Invalid();
                ValidateNode(node, Layout + (media ? " LoadedBehavior UnloadedBehavior Position SpeedRatio Volume Balance IsMuted ScrubbingEnabled" : ""));
                var element = new Element
                {
                    Id = ReadId(node,"ObjectId") ?? Guid.NewGuid(),
                    IsMedia = media,
                    Name = (string)node.Attribute("Name") ?? ("ele_" + Guid.NewGuid().ToString("N")),
                    Width = Dimension(node, "Width"), Height = Dimension(node, "Height"),
                    Left = Number(node, "InkCanvas.Left", double.NaN), Right = Number(node, "InkCanvas.Right", double.NaN),
                    Top = Number(node, "InkCanvas.Top", double.NaN), Bottom = Number(node, "InkCanvas.Bottom", double.NaN),
                    Opacity = Number(node, "Opacity", 1), Transform = ParseMatrix((string)node.Attribute("RenderTransform") ?? "Identity"),
                    Origin = Point.Parse((string)node.Attribute("RenderTransformOrigin") ?? "0,0")
                };
                var z = (string)node.Attribute("Panel.ZIndex");
                if (!objectIds.Add(element.Id) || (z != null && !int.TryParse(z, NumberStyles.Integer, Invariant, out element.Z))) throw Invalid();
                if (!names.Add(element.Name) || element.Opacity < 0 || element.Opacity > 1 ||
                    !double.IsFinite(element.Origin.X) || !double.IsFinite(element.Origin.Y)) throw Invalid();
                if (!Enum.TryParse((string)node.Attribute("Stretch") ?? "Uniform", out element.Stretch) ||
                    !Enum.IsDefined(element.Stretch)) throw Invalid();
                string source = (string)node.Attribute("Source");
                bool hasTransform = node.Attribute("RenderTransform") != null;
                foreach (var property in node.Elements())
                {
                    ValidateNode(property, "");
                    if (property.Name == Presentation + (node.Name.LocalName + ".RenderTransform") && !hasTransform)
                    {
                        element.Transform = ReadTransform(OnlyChild(property), 0);
                        hasTransform = true;
                    }
                    else if (!media && property.Name == Presentation + "Image.Source" && source == null)
                    {
                        var bitmap = OnlyChild(property);
                        if (bitmap.Name == Presentation + "BitmapImage")
                        {
                            ValidateLeaf(bitmap, "UriSource CacheOption BaseUri");
                            source = (string)bitmap.Attribute("UriSource");
                        }
                        else if (bitmap.Name == Presentation + "TransformedBitmap")
                        {
                            ValidateNode(bitmap, "Source");
                            source = (string)bitmap.Attribute("Source");
                            var transform = OnlyChild(bitmap, "TransformedBitmap.Transform");
                            ValidateNode(transform, "");
                            // Legacy inserts already store the displayed width/height on Image.
                            // The packaged original can be scaled to that same display size.
                            var scale = OnlyChild(transform, "ScaleTransform");
                            ValidateLeaf(scale, "ScaleX ScaleY");
                        }
                        else throw Invalid();
                    }
                    else throw Invalid();
                }
                element.Asset = GetArchiveReference(source);
                result.Add(element);
                if (result.Count > CanvasDocument.MaxEntries - 2) throw Invalid();
            }
            return result;
        }

        private static string GetArchiveReference(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("作品中的元素缺少资源引用。");
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw Invalid();
                // Old writers embedded a machine-specific absolute path. Only its basename
                // has meaning here. Never open that path, even if it exists on this machine.
                source = Path.GetFileName(uri.LocalPath);
            }
            else
            {
                source = source.Replace('\\', '/');
                const string prefix = "File Dependency/";
                if (source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) source = source.Substring(prefix.Length);
            }
            if (!CanvasDocument.IsAssetName(source)) throw Invalid();
            return "File Dependency/" + source;
        }

        private static XElement OnlyChild(XElement node, string name = null)
        {
            var child = node.Elements().SingleOrDefault();
            if (child == null || (name != null && child.Name != Presentation + name)) throw Invalid();
            return child;
        }

        private static void ValidateNode(XElement node, string attributes)
        {
            if (node.Name.Namespace != Presentation || node.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) throw Invalid();
            foreach (var attribute in node.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    if (attribute.Value != Presentation.NamespaceName && attribute.Value != "http://schemas.microsoft.com/winfx/2006/xaml") throw Invalid();
                }
                else
                {
                    if (attribute.Name.Namespace != XNamespace.None ||
                        !(" " + attributes + " ").Contains(" " + attribute.Name.LocalName + " ", StringComparison.Ordinal)) throw Invalid();
                    // WPF serializes an unset BitmapImage.BaseUri as this literal sentinel.
                    // Discard it; all resources are looked up in the current archive.
                    if (attribute.Name.LocalName == "BaseUri")
                    {
                        if (attribute.Value != "{x:Null}") throw Invalid();
                    }
                    else if (attribute.Value.AsSpan().TrimStart().StartsWith("{")) throw Invalid();
                }
            }
        }

        private static void ValidateLeaf(XElement node, string attributes)
        {
            ValidateNode(node, attributes);
            if (node.HasElements) throw Invalid();
        }

        private static double Number(XElement node, string name, double fallback = 0)
        {
            var attr = node.Attribute(name);
            if (attr == null) return fallback;
            if (!double.TryParse(attr.Value, NumberStyles.Float, Invariant, out double value) || !double.IsFinite(value)) throw Invalid();
            return value;
        }

        private static double Dimension(XElement node, string name)
        {
            if ((string)node.Attribute(name) == "Auto") return double.NaN;
            var value = Number(node, name, double.NaN);
            if (value < 0 || value > 1_000_000) throw Invalid();
            return value;
        }

        private static Matrix ParseMatrix(string text)
        {
            var matrix = Matrix.Parse(text);
            if (!double.IsFinite(matrix.M11) || !double.IsFinite(matrix.M12) || !double.IsFinite(matrix.M21) ||
                !double.IsFinite(matrix.M22) || !double.IsFinite(matrix.OffsetX) || !double.IsFinite(matrix.OffsetY)) throw Invalid();
            return matrix;
        }

        private static Matrix ReadTransform(XElement node, int depth)
        {
            if (depth > 24 || node.Name.Namespace != Presentation) throw Invalid();
            Matrix matrix = Matrix.Identity;
            switch (node.Name.LocalName)
            {
                case "TransformGroup":
                    ValidateNode(node, "");
                    var children = node.Elements();
                    if (node.Element(Presentation + "TransformGroup.Children") != null)
                    {
                        var collection = OnlyChild(node, "TransformGroup.Children");
                        ValidateNode(collection, "");
                        children = collection.Elements();
                    }
                    foreach (var child in children) matrix.Append(ReadTransform(child, depth + 1));
                    break;
                case "MatrixTransform":
                    ValidateLeaf(node, "Matrix");
                    return ParseMatrix((string)node.Attribute("Matrix") ?? "Identity");
                case "ScaleTransform":
                    ValidateLeaf(node, "ScaleX ScaleY CenterX CenterY");
                    matrix.ScaleAt(Number(node, "ScaleX", 1), Number(node, "ScaleY", 1), Number(node, "CenterX"), Number(node, "CenterY"));
                    break;
                case "TranslateTransform":
                    ValidateLeaf(node, "X Y");
                    matrix.Translate(Number(node, "X"), Number(node, "Y"));
                    break;
                case "RotateTransform":
                    ValidateLeaf(node, "Angle CenterX CenterY");
                    matrix.RotateAt(Number(node, "Angle"), Number(node, "CenterX"), Number(node, "CenterY"));
                    break;
                default: throw Invalid();
            }
            return ParseMatrix(matrix.ToString(Invariant));
        }

        private static Guid? ReadId(XElement node,string name)
        {
            var value = (string)node.Attribute(name);
            if (value == null) return null;
            if (!Guid.TryParseExact(value,"D",out Guid id) || id == Guid.Empty) throw Invalid();
            return id;
        }

        internal static XElement WriteElement(FrameworkElement item, string asset, Stretch stretch, SceneObject state)
        {
            var result = new XElement(Presentation + (item is MediaElement ? "MediaElement" : "Image"),
                new XAttribute("Source", asset), new XAttribute("Stretch", stretch),
                new XAttribute("ObjectId",state.Id.ToString("D")), new XAttribute("Panel.ZIndex",state.Z),
                new XAttribute("Opacity", state.Opacity), new XAttribute("RenderTransform", state.Matrix.ToString(Invariant)),
                new XAttribute("RenderTransformOrigin", new Point(state.OriginX,state.OriginY).ToString(Invariant)));
            if (!string.IsNullOrEmpty(item.Name)) result.Add(new XAttribute("Name", item.Name));
            void Add(string name, double value)
            {
                if (double.IsFinite(value)) result.Add(new XAttribute(name, value));
            }
            Add("Width", state.Width); Add("Height", state.Height);
            Add("InkCanvas.Left", state.Left); Add("InkCanvas.Top", state.Top);
            Add("InkCanvas.Right", state.Right); Add("InkCanvas.Bottom", state.Bottom);
            return result;
        }

        private static InvalidDataException Invalid() => new InvalidDataException("作品含有损坏或不受支持的元素属性、类型或资源引用。");
    }
}
