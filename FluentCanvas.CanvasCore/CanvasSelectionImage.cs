using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluentCanvas.Helpers
{
    internal static class CanvasSelectionImage
    {
        private const double Padding = 5;
        private const int MaxDimension = 16384;
        private const int MaxPixels = 32 * 1024 * 1024;

        internal static RenderTargetBitmap Render(InkCanvas canvas)
        {
            canvas.UpdateLayout();
            var elements = new HashSet<UIElement>(canvas.GetSelectedElements());
            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
            {
                foreach (var element in canvas.Children.Cast<UIElement>().Where(elements.Contains).OrderBy(Panel.GetZIndex))
                {
                    if (element.Visibility != Visibility.Visible) continue;
                    var bounds = VisualTreeHelper.GetDescendantBounds(element);
                    var clip = VisualTreeHelper.GetClip(element);
                    if (clip != null) bounds.Intersect(clip.Bounds);
                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;

                    var visualTransform = VisualTreeHelper.GetTransform(element);
                    if (visualTransform != null) bounds = visualTransform.TransformBounds(bounds);
                    bounds.Offset(VisualTreeHelper.GetOffset(element));
                    if (bounds.Width <= 0 || bounds.Height <= 0) continue;

                    // VisualBrush already includes the source visual's own transform and layout offset.
                    var paint = new VisualBrush(element)
                    {
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = bounds,
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = bounds,
                        Stretch = Stretch.None
                    };
                    var parent = (Visual)VisualTreeHelper.GetParent(element);
                    var parentTransform = parent == canvas ? Transform.Identity : (Transform)parent.TransformToAncestor(canvas);
                    context.PushTransform(parentTransform);
                    context.DrawRectangle(paint, null, bounds);
                    context.Pop();
                }

                foreach (var stroke in canvas.GetSelectedStrokes())
                    stroke.Draw(context);
            }
            var drawingBounds = drawing.Bounds;
            if (drawingBounds.IsEmpty) return null;

            double width = Math.Ceiling(drawingBounds.Width + Padding * 2);
            double height = Math.Ceiling(drawingBounds.Height + Padding * 2);
            if (!double.IsFinite(width) || !double.IsFinite(height) ||
                width > MaxDimension || height > MaxDimension || width * height > MaxPixels)
                throw new InvalidOperationException("选区太大，无法导出为单张图片。");

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(Padding - drawingBounds.X, Padding - drawingBounds.Y));
                context.DrawDrawing(drawing);
            }
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
