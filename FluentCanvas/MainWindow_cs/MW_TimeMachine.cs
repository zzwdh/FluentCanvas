using FluentCanvas.Helpers;
using System.Collections.Generic;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;

using System.Windows.Media;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        private enum CommitReason
        {
            UserInput,
            CodeInput,
            ShapeDrawing,
            ShapeRecognition,
            Manipulation,
            ImageInsert
        }

        private CommitReason _currentCommitType = CommitReason.UserInput;
        private long canvasContentVersion;
        private bool IsEraseByPoint => inkCanvas.EditingMode == InkCanvasEditingMode.EraseByPoint;
        private StrokeCollection ReplacedStroke;
        private IndexedEraseInput eraseInput;
        private IndexedInkErase indexedErase;
        private Point? previousErasePoint;

        internal void InitializeIndexedEraser()
        {
            eraseInput = new IndexedEraseInput(this, inkCanvas, BeginIndexedErase, AdvanceIndexedErase, FinishIndexedErase);
        }

        internal void BeginIndexedErase(double diameter, bool wholeStroke)
        {
            CommitPendingCanvasEdits();
            previousErasePoint = null;
            indexedErase = timeMachine.StartErase(inkCanvas.Strokes, diameter, wholeStroke);
        }

        internal void AdvanceIndexedErase(Point point)
        {
            if (previousErasePoint == point || indexedErase == null) return;
            var reason = _currentCommitType;
            _currentCommitType = CommitReason.CodeInput;
            try { indexedErase.Sweep(previousErasePoint ?? point, point); previousErasePoint = point; }
            catch
            {
                var interrupted = indexedErase;
                indexedErase = null;
                interrupted.RestoreProjection();
                eraseInput?.Complete();
                throw;
            }
            finally { _currentCommitType = reason; }
        }

        internal void FinishIndexedErase()
        {
            var finishing = indexedErase;
            indexedErase = null;
            previousErasePoint = null;
            if (finishing == null) return;
            try { finishing.Finish(timeMachine); }
            catch
            {
                var reason = _currentCommitType;
                _currentCommitType = CommitReason.CodeInput;
                try { finishing.RestoreProjection(); }
                finally { _currentCommitType = reason; }
                throw;
            }
        }
        private StrokeCollection AddedStroke;
        private readonly HashSet<Stroke> strokeManipulationHistory = new();
        private Stroke finishingPressure;
        private Dictionary<string, Tuple<object, TransformGroup>> ElementsManipulationHistory;

        // object maybe TransformGroup or ElementData
        private Dictionary<string, object> ElementsInitialHistory = new Dictionary<string, object>();

        private readonly HashSet<Stroke> DrawingAttributesHistory = new();
        private TimeMachine timeMachine = new TimeMachine();

        public UIElement GetElementByTimestamp(InkCanvas inkCanvas, string timestamp)
        {
            foreach (UIElement child in inkCanvas.Children)
            {
                if (child is FrameworkElement frameworkElement && frameworkElement.Name == timestamp)
                {
                    return child;
                }
            }
            return null;
        }

        private void ApplyHistoryToCanvas(HistoryTransition transition)
        {
            var previous = _currentCommitType;
            _currentCommitType = CommitReason.CodeInput;
            try { ReplayHistoryEntry(transition); }
            catch
            {
                // Roll the native cursor back if a WPF element refuses to attach, then
                // restore the original view for any ink already projected by this edit.
                var rollback = transition.Direction == HistoryDirection.Undo ? timeMachine.Redo() : timeMachine.Undo();
                ReplayHistoryEntry(rollback, reconcile: true);
                throw;
            }
            finally { _currentCommitType = previous; }
        }

        private void ReplayHistoryEntry(HistoryTransition transition, bool reconcile = false)
        {
            var item = transition.Entry;
            transition.ProjectInk(inkCanvas, reconcile);
            transition.ProjectElements(inkCanvas);
            if (item.GestureMedia != null)
                foreach (var change in item.GestureMedia)
                    if (change.Element is FrameworkElement element) ElementsInitialHistory.Remove(element.Name);
            if (item.CommitType == TimeMachineHistoryType.Manipulation) UpdateTransformedCircles();
            canvasContentVersion++;
        }

        private void TimeMachine_OnUndoStateChanged(bool status)
        {
            CommandManager.InvalidateRequerySuggested();
        }

        private void TimeMachine_OnRedoStateChanged(bool status)
        {
            CommandManager.InvalidateRequerySuggested();
        }

        private void StrokesOnStrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
        {
            committedPreview?.Dispose();
            canvasContentVersion++;
            CancelContentGesture();
            if (e.Removed.Count > 0 && _currentCommitType != CommitReason.ShapeRecognition)
                InvalidateInkRecognition();
            if (!isHidingSubPanelsWhenInking)
            {
                isHidingSubPanelsWhenInking = true;
                HideSubPanels(); // 书写时自动隐藏二级菜单
            }

            // Preserve the preceding manipulation before an eraser removes or splits its render object.
            if (_currentCommitType != CommitReason.CodeInput && e.Removed.Count > 0)
                CommitPendingStrokeDrawingAttributes();

            foreach (var stroke in e.Removed)
            {
                stroke.StylusPointsChanged -= Stroke_StylusPointsChanged;
                stroke.StylusPointsReplaced -= Stroke_StylusPointsReplaced;
                stroke.DrawingAttributesChanged -= Stroke_DrawingAttributesChanged;
            }
            foreach (var stroke in e.Added)
            {
                stroke.StylusPointsChanged += Stroke_StylusPointsChanged;
                stroke.StylusPointsReplaced += Stroke_StylusPointsReplaced;
                stroke.DrawingAttributesChanged += Stroke_DrawingAttributesChanged;
            }
            if (_currentCommitType is CommitReason.CodeInput or CommitReason.ShapeDrawing or CommitReason.ShapeRecognition) return;
            if ((e.Added.Count != 0 || e.Removed.Count != 0) && IsEraseByPoint)
            {
                if (AddedStroke == null) AddedStroke = new StrokeCollection();
                if (ReplacedStroke == null) ReplacedStroke = new StrokeCollection();
                AddedStroke.Add(e.Added);
                ReplacedStroke.Add(e.Removed);
                return;
            }
            if (e.Added.Count > 0 && e.Removed.Count > 0) timeMachine.CommitStrokeShapeHistory(e.Removed, e.Added);
            else if (e.Added.Count > 0) timeMachine.CommitStrokeUserInputHistory(e.Added);
            else if (e.Removed.Count > 0) timeMachine.CommitStrokeEraseHistory(e.Removed);
        }

        private void Stroke_DrawingAttributesChanged(object sender, PropertyDataChangedEventArgs e)
        {
            committedPreview?.Dispose();
            canvasContentVersion++;
            if (_currentCommitType == CommitReason.CodeInput) return;
            CancelContentGesture();
            DrawingAttributesHistory.Add((Stroke)sender);
        }

        // Pressure simulation finishes after StrokeCollected and may resume after recognition.
        // Amend the existing native stroke so undo still removes the complete pen input in one step.
        internal void FinishCollectedStroke(Stroke stroke, int style, double elapsedMilliseconds)
        {
            if (!inkCanvas.Strokes.Contains(stroke)) return;
            FinishContentGesture();
            if (strokeManipulationHistory.Contains(stroke)) ToCommitStrokeManipulationHistoryAfterMouseUp();
            finishingPressure = stroke;
            try { timeMachine.FinishCollectedStroke(stroke, style, elapsedMilliseconds); }
            finally { finishingPressure = null; }
        }

        private void Stroke_StylusPointsReplaced(object sender, StylusPointsReplacedEventArgs e) =>
            TrackGeometryChange((Stroke)sender);

        private void Stroke_StylusPointsChanged(object sender, EventArgs e) => TrackGeometryChange((Stroke)sender);

        private void TrackGeometryChange(Stroke stroke)
        {
            committedPreview?.Dispose();
            canvasContentVersion++;
            if (_currentCommitType == CommitReason.CodeInput || stroke == finishingPressure) return;
            CancelContentGesture();
            strokeManipulationHistory.Add(stroke);
            if (dec.Count > 0 || isGridInkCanvasSelectionCoverMouseDown) return;

            var selectedStrokes = inkCanvas.GetSelectedStrokes();
            int count = selectedStrokes.Count > 0 ? selectedStrokes.Count : inkCanvas.Strokes.Count;
            if (strokeManipulationHistory.Count == count)
            {
                ToCommitStrokeManipulationHistoryAfterMouseUp();
            }
        }

        private void ToCommitStrokeManipulationHistoryAfterMouseUp()
        {
            FinishContentGesture();
            if (strokeManipulationHistory.Count == 0 && !(ElementsManipulationHistory?.Count > 0)) return;

            strokeManipulationHistory.UnionWith(DrawingAttributesHistory);
            var changes = new List<GestureMediaChange>();
            if (ElementsManipulationHistory != null)
                foreach (var (key, pair) in ElementsManipulationHistory)
                {
                    var element = GetElementByTimestamp(inkCanvas, key);
                    if (element == null) continue;
                    changes.Add(new GestureMediaChange(element, pair.Item1 as Transform ?? Transform.Identity,
                        pair.Item2, pair.Item1 is FluentCanvasElementsHelper.ElementData));
                }
            timeMachine.CommitTransformedObjects(strokeManipulationHistory, changes.ToArray());
            DrawingAttributesHistory.Clear();
            strokeManipulationHistory.Clear();
            if (ElementsManipulationHistory != null)
            {
                foreach (var item in ElementsManipulationHistory)
                    ElementsInitialHistory[item.Key] = item.Value.Item2;
                ElementsManipulationHistory = null;
            }
        }

        private void CommitPendingStrokeErasure()
        {
            eraseInput?.Complete();
            FinishIndexedErase();
            if (ReplacedStroke == null && AddedStroke == null) return;
            timeMachine.CommitStrokeEraseHistory(ReplacedStroke, AddedStroke);
            ReplacedStroke = null;
            AddedStroke = null;
        }

        private void CommitPendingCanvasEdits()
        {
            CommitPendingStrokeErasure();
            CommitPendingStrokeDrawingAttributes();
        }

        private void CommitPendingStrokeDrawingAttributes()
        {
            ToCommitStrokeManipulationHistoryAfterMouseUp();
            if (DrawingAttributesHistory.Count == 0) return;
            timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
            DrawingAttributesHistory.Clear();
        }
    }
}
