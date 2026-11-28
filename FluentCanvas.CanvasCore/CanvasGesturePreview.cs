using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentCanvas.Helpers;

// Only the presenter's ink rendering is substituted. The canvas remains the
// capture/input surface. Media keep their live visual, playback and z-order.
internal sealed class CanvasGesturePreview : IDisposable
{
    private readonly InkPresenter presenter;
    private readonly StrokeCollection original;
    private readonly List<DrawingVisual> layers = new();
    private readonly Dictionary<UIElement, Transform> mediaTransforms = new();
    private readonly MatrixTransform movement = new();
    private readonly List<(Adorner Adorner, Transform Original)> adorners = new();
    private bool substituted;
    private int prepared;
    private Action handoffComplete;

    internal readonly Stroke[] Strokes;
    internal readonly UIElement[] Elements;
    internal Matrix Matrix => movement.Matrix;
    internal BrushScale Width { get; private set; } = BrushScale.Width;
    internal BrushScale Height { get; private set; } = BrushScale.Height;
    internal Rect SelectionBounds { get; }
    internal bool MovesSelection { get; }

    internal CanvasGesturePreview(InkPresenter presenter, IEnumerable<Stroke> strokes, IEnumerable<UIElement> elements,
        Rect selectionBounds, bool movesSelection)
    {
        this.presenter = presenter;
        original = presenter.Strokes;
        Strokes = strokes.ToArray();
        Elements = elements.ToArray();
        SelectionBounds = selectionBounds;
        MovesSelection = movesSelection;
        try
        {
            if (Strokes.Length > 0) ShowInkLayers();
            foreach (var element in Elements)
            {
                mediaTransforms.Add(element, element.RenderTransform);
                element.SetCurrentValue(UIElement.RenderTransformProperty, MovingFrom(element));
            }
            if (movesSelection && presenter.Child is UIElement inner)
            {
                var attached = AdornerLayer.GetAdornerLayer(inner)?.GetAdorners(inner);
                if (attached != null)
                    foreach (var adorner in attached)
                    {
                        adorners.Add((adorner, adorner.RenderTransform));
                        adorner.SetCurrentValue(UIElement.RenderTransformProperty, MovingFrom(adorner));
                    }
            }
        }
        catch { Dispose(); throw; }
    }

    // A RenderTransform is local to the arranged child. Conjugate the board
    // matrix by the layout position and render origin so a rotated image and its
    // annotations continue to share the same board-space pivot.
    private static Vector Anchor(UIElement element)
    {
        var origin = element.RenderTransformOrigin;
        return VisualTreeHelper.GetOffset(element) +
            new Vector(element.RenderSize.Width * origin.X, element.RenderSize.Height * origin.Y);
    }

    internal Matrix FinalMediaMatrix(UIElement element)
    {
        var previous = element.RenderTransform?.Value ?? Matrix.Identity;
        var anchor = Anchor(element);
        previous.Translate(anchor.X, anchor.Y);
        previous.Append(Matrix);
        previous.Translate(-anchor.X, -anchor.Y);
        return previous;
    }

    private TransformGroup MovingFrom(UIElement element)
    {
        var result = new TransformGroup();
        var previous = element.RenderTransform;
        var anchor = Anchor(element);
        if (previous != null && !previous.Value.IsIdentity) result.Children.Add(previous);
        if (anchor.X != 0 || anchor.Y != 0) result.Children.Add(new TranslateTransform(anchor.X, anchor.Y));
        result.Children.Add(movement);
        if (anchor.X != 0 || anchor.Y != 0) result.Children.Add(new TranslateTransform(-anchor.X, -anchor.Y));
        return result;
    }

    private void ShowInkLayers()
    {
        var moving = Strokes.ToHashSet();
        // InkPresenter composites equal-color highlighters together. Keep the
        // paint order inside each group even when only every other stroke moves.
        bool highContrast = SystemParameters.HighContrast;
        foreach (var group in original.Where(s => !highContrast || !s.DrawingAttributes.IsHighlighter)
            .GroupBy(s => s.DrawingAttributes.IsHighlighter
                ? (Color?)Color.FromRgb(s.DrawingAttributes.Color.R, s.DrawingAttributes.Color.G, s.DrawingAttributes.Color.B)
                : null))
        {
            Brush brush = highContrast ? SystemColors.WindowTextBrush : null;
            if (group.Key is Color color)
            {
                var solid = new SolidColorBrush(color);
                solid.Freeze();
                brush = solid;
            }
            var runs = new ContainerVisual();
            DrawingContext context = null;
            bool lastMoved = false;
            try
            {
                foreach (var stroke in group)
                {
                    bool moved = moving.Contains(stroke);
                    if (context == null || moved != lastMoved)
                    {
                        context?.Close();
                        var run = new DrawingVisual();
                        if (moved) run.Transform = movement;
                        runs.Children.Add(run);
                        context = run.RenderOpen();
                        lastMoved = moved;
                    }
                    if (brush == null) stroke.Draw(context);
                    else context.DrawGeometry(brush, null, stroke.GetGeometry(stroke.DrawingAttributes));
                }
            }
            finally { context?.Close(); }
            var layer = new DrawingVisual();
            layer.Children.Add(runs);
            presenter.AttachVisuals(layer, group.First().DrawingAttributes);
            layers.Add(layer);
        }
        // Retain WPF's one-way binding to InkCanvas.Strokes and its editing/selection collection.
        presenter.SetCurrentValue(InkPresenter.StrokesProperty, new StrokeCollection());
        substituted = true;
    }

    internal void Append(Matrix delta, double scaleX, double scaleY)
    {
        var matrix = movement.Matrix;
        matrix.Append(delta);
        if (!double.IsFinite(matrix.M11) || !double.IsFinite(matrix.M12) || !double.IsFinite(matrix.M21) ||
            !double.IsFinite(matrix.M22) || !double.IsFinite(matrix.OffsetX) || !double.IsFinite(matrix.OffsetY))
            throw new ArgumentOutOfRangeException(nameof(delta));
        Width = Width.Then(scaleX, DrawingAttributes.MinWidth, DrawingAttributes.MaxWidth);
        Height = Height.Then(scaleY, DrawingAttributes.MinHeight, DrawingAttributes.MaxHeight);
        movement.Matrix = matrix;
    }

    internal void RestoreMedia()
    {
        foreach (var (element, transform) in mediaTransforms)
            element.SetCurrentValue(UIElement.RenderTransformProperty, transform);
        mediaTransforms.Clear();
    }

    // The preview already shows the committed matrix. Reattaching a large
    // collection cold makes InkPresenter rebuild all its outlines in one call.
    // Prime WPF's per-stroke geometry cache in bounded batches across frames.
    internal bool BeginHandoff(Action complete)
    {
        if (Strokes.Length < 128 || !presenter.IsVisible) return false;
        handoffComplete = complete;
        RestoreAdorners();
        presenter.Unloaded += PresenterUnloaded;
        presenter.IsVisibleChanged += VisibilityChanged;
        CompositionTarget.Rendering += PrepareFrame;
        return true;
    }

    private void PrepareFrame(object sender, EventArgs args)
    {
        long start = Stopwatch.GetTimestamp();
        while (prepared < Strokes.Length)
        {
            var stroke = Strokes[prepared++];
            _ = stroke.GetGeometry(stroke.DrawingAttributes);
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 2) return;
        }
        Dispose();
    }

    private void PresenterUnloaded(object sender, RoutedEventArgs args) => Dispose();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!presenter.IsVisible) Dispose();
    }

    private void RestoreAdorners()
    {
        foreach (var (adorner, transform) in adorners)
            adorner.SetCurrentValue(UIElement.RenderTransformProperty, transform);
        adorners.Clear();
    }

    public void Dispose()
    {
        if (handoffComplete != null)
        {
            CompositionTarget.Rendering -= PrepareFrame;
            presenter.Unloaded -= PresenterUnloaded;
            presenter.IsVisibleChanged -= VisibilityChanged;
            handoffComplete();
            handoffComplete = null;
        }
        RestoreMedia();
        RestoreAdorners();
        foreach (var layer in layers) presenter.DetachVisuals(layer);
        layers.Clear();
        if (substituted) presenter.SetCurrentValue(InkPresenter.StrokesProperty, original);
        substituted = false;
    }

    // clamp(clamp(w*a, lo, hi)*b) can be represented by just three values,
    // including a gesture that hits a width bound and then reverses direction.
    internal readonly record struct BrushScale(double Factor, double Low, double High)
    {
        internal static BrushScale Width => new(1, DrawingAttributes.MinWidth, DrawingAttributes.MaxWidth);
        internal static BrushScale Height => new(1, DrawingAttributes.MinHeight, DrawingAttributes.MaxHeight);
        internal BrushScale Then(double scale, double min, double max)
        {
            double factor = Factor * scale;
            if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
            return new(factor, Math.Clamp(Low * scale, min, max), Math.Clamp(High * scale, min, max));
        }
    }
}
