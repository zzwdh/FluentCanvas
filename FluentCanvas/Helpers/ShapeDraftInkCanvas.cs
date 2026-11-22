#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Threading;

namespace FluentCanvas;

internal sealed class ShapeDraftInkCanvas : InkCanvas
{
    private readonly List<DrawingVisual> attachedDrafts = new();
    private StrokeCollection? stagedStrokes;
    private StrokeCollection? draftStrokes;
    private DispatcherOperation? pendingRender;

    public ShapeDraftInkCanvas()
    {
        Loaded += (_, _) => SystemParameters.StaticPropertyChanged += OnSystemPropertyChanged;
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemPropertyChanged;
            SetShapePreview(null, null);
        };
    }

    internal void SetShapePreview(StrokeCollection? staged, StrokeCollection? draft)
    {
        if (ReferenceEquals(stagedStrokes, staged) && ReferenceEquals(draftStrokes, draft)) return;
        stagedStrokes = staged;
        draftStrokes = draft;
        if (staged == null && draft == null)
        {
            pendingRender?.Abort();
            pendingRender = null;
            DetachDrafts();
        }
        else ScheduleRender();
    }

    private void OnSystemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(() =>
            {
                if (stagedStrokes != null || draftStrokes != null) ScheduleRender();
            }, DispatcherPriority.Render);
    }

    private void ScheduleRender() => pendingRender ??= Dispatcher.InvokeAsync(RenderShapePreview, DispatcherPriority.Render);

    private void RenderShapePreview()
    {
        pendingRender = null;
        DetachDrafts();

        IEnumerable<Stroke> staged = stagedStrokes ?? Enumerable.Empty<Stroke>();
        IEnumerable<Stroke> drawing = draftStrokes ?? Enumerable.Empty<Stroke>();
        bool highContrast = SystemParameters.HighContrast;
        var groups = staged.Concat(drawing)
            .Where(stroke => !highContrast || !stroke.DrawingAttributes.IsHighlighter)
            .GroupBy(stroke => stroke.DrawingAttributes.IsHighlighter
                ? (Color?)Color.FromRgb(stroke.DrawingAttributes.Color.R, stroke.DrawingAttributes.Color.G, stroke.DrawingAttributes.Color.B)
                : null);

        foreach (var group in groups)
        {
            var visual = new ShapeStrokeVisual();
            using (var context = visual.RenderOpen())
            {
                Brush? brush = highContrast ? SystemColors.WindowTextBrush : null;
                if (group.Key is Color color)
                {
                    // InkPresenter applies the shared highlighter opacity to this visual and the
                    // existing same-color ink. Render the contents with solid color as WPF does.
                    var highlighterBrush = new SolidColorBrush(color);
                    highlighterBrush.Freeze();
                    brush = highlighterBrush;
                }

                foreach (var stroke in group)
                {
                    if (brush == null) stroke.Draw(context);
                    else context.DrawGeometry(brush, null, stroke.GetGeometry(stroke.DrawingAttributes));
                }
            }
            InkPresenter.AttachVisuals(visual, group.First().DrawingAttributes);
            attachedDrafts.Add(visual);
        }
    }

    private void DetachDrafts()
    {
        foreach (var visual in attachedDrafts) InkPresenter.DetachVisuals(visual);
        attachedDrafts.Clear();
    }

    private sealed class ShapeStrokeVisual : DrawingVisual
    {
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
        protected override GeometryHitTestResult? HitTestCore(GeometryHitTestParameters hitTestParameters) => null;
    }
}
