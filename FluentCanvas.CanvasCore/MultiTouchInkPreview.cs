using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentCanvas.Helpers
{
    internal sealed class MultiTouchInkPreview(InkCanvas canvas)
    {
        private readonly Dictionary<int, StrokePreview> _contacts = new();
        private bool _renderScheduled;

        internal static bool IsTransient(UIElement child) => child is StrokePreview;

        internal void Begin(int contactId, StylusPointCollection points)
        {
            Cancel(contactId);
            if (points.Count == 0) return;

            var preview = new StrokePreview(this, contactId, new Stroke(points, canvas.DefaultDrawingAttributes.Clone()));
            canvas.Children.Add(preview);
            _contacts.Add(contactId, preview);
            QueueRender();
        }

        internal bool Contains(int contactId) => _contacts.ContainsKey(contactId);

        internal void Append(int contactId, StylusPointCollection points)
        {
            if (points.Count == 0 || !_contacts.TryGetValue(contactId, out var preview)) return;

            preview.Stroke.StylusPoints.Add(points);
            preview.NeedsRedraw = true;
            QueueRender();
        }

        internal Stroke Complete(int contactId, StylusPointCollection points)
        {
            if (!_contacts.TryGetValue(contactId, out var preview)) return null;

            try
            {
                if (points.Count > 0) preview.Stroke.StylusPoints.Add(points);
                // Update the committed ink and remove the preview in the same dispatcher turn.
                canvas.Strokes.Add(preview.Stroke);
                return preview.Stroke;
            }
            finally
            {
                Cancel(contactId);
            }
        }

        internal void Cancel(int contactId)
        {
            if (_contacts.Remove(contactId, out var preview)) canvas.Children.Remove(preview);
            if (_contacts.Count == 0) StopRender();
        }

        internal void CancelAll()
        {
            foreach (int contactId in _contacts.Keys.ToArray()) Cancel(contactId);
        }

        private void ForgetDetached(int contactId, StrokePreview detached)
        {
            if (_contacts.TryGetValue(contactId, out var preview) && ReferenceEquals(detached, preview))
                _contacts.Remove(contactId);
            if (_contacts.Count == 0) StopRender();
        }

        private void RemoveExpiredPreview(StrokePreview preview) => canvas.Children.Remove(preview);

        private void QueueRender()
        {
            if (_renderScheduled) return;

            CompositionTarget.Rendering += RenderPending;
            _renderScheduled = true;
        }

        private void StopRender()
        {
            if (!_renderScheduled) return;

            CompositionTarget.Rendering -= RenderPending;
            _renderScheduled = false;
        }

        private void RenderPending(object sender, EventArgs e)
        {
            StopRender();
            foreach (var preview in _contacts.Values)
            {
                if (preview.NeedsRedraw) preview.Redraw();
            }
        }

        private sealed class StrokePreview : FrameworkElement
        {
            private readonly MultiTouchInkPreview _owner;
            private readonly int _contactId;
            private readonly DrawingVisual _visual = new();
            private bool _detached;

            internal StrokePreview(MultiTouchInkPreview owner, int contactId, Stroke stroke)
            {
                _owner = owner;
                _contactId = contactId;
                Stroke = stroke;
                IsHitTestVisible = false;
                AddVisualChild(_visual);
            }

            internal Stroke Stroke { get; }
            internal bool NeedsRedraw { get; set; } = true;
            protected override int VisualChildrenCount => 1;
            protected override Visual GetVisualChild(int index) => _visual;

            protected override void OnVisualParentChanged(DependencyObject oldParent)
            {
                base.OnVisualParentChanged(oldParent);
                if (oldParent != null && VisualParent == null)
                {
                    _detached = true;
                    using (_visual.RenderOpen()) { }
                    _owner.ForgetDetached(_contactId, this);
                }
                else if (_detached && VisualParent != null)
                {
                    // A document rollback may restore the old child list. Discard the expired preview.
                    Dispatcher.InvokeAsync(() => _owner.RemoveExpiredPreview(this));
                }
            }

            internal void Redraw()
            {
                using var drawingContext = _visual.RenderOpen();
                Stroke.Draw(drawingContext);
                NeedsRedraw = false;
            }
        }
    }
}
