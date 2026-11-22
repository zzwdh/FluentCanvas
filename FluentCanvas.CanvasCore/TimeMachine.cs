using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentCanvas.Helpers
{
    public class TimeMachine
    {
        private List<TimeMachineHistory> _currentStrokeHistory = new List<TimeMachineHistory>();

        private int _currentIndex = -1;
        private object _origin = new();
        private object _savedState;

        internal bool CanUndo => _currentIndex >= 0;
        internal bool CanRedo => _currentIndex < _currentStrokeHistory.Count - 1;
        internal object CurrentState => CanUndo ? _currentStrokeHistory[_currentIndex] : _origin;
        internal bool HasUnsavedChanges => !ReferenceEquals(CurrentState, _savedState ?? _origin);

        internal sealed record Snapshot(TimeMachineHistory[] Entries, int Index, object Origin, object SavedState)
        {
            internal bool HasUnsavedChanges => !ReferenceEquals(Index < 0 ? Origin : Entries[Index], SavedState ?? Origin);
        }

        internal void MarkSaved() => _savedState = CurrentState;

        public delegate void OnUndoStateChange(bool status);

        public event OnUndoStateChange OnUndoStateChanged;

        public delegate void OnRedoStateChange(bool status);

        public event OnRedoStateChange OnRedoStateChanged;

        private void Commit(TimeMachineHistory item)
        {
            if (_currentIndex + 1 < _currentStrokeHistory.Count)
            {
                _currentStrokeHistory.RemoveRange(_currentIndex + 1, (_currentStrokeHistory.Count - 1) - _currentIndex);
            }
            _currentStrokeHistory.Add(item);
            _currentIndex = _currentStrokeHistory.Count - 1;
            NotifyUndoRedoState();
        }

        public void CommitStrokeUserInputHistory(StrokeCollection stroke)
        {
            Commit(new TimeMachineHistory(stroke, TimeMachineHistoryType.UserInput, false));
        }

        public void CommitStrokeShapeHistory(StrokeCollection strokeToBeReplaced, StrokeCollection generatedStroke)
        {
            Commit(new TimeMachineHistory(generatedStroke, TimeMachineHistoryType.ShapeRecognition, false, strokeToBeReplaced));
        }

        public void CommitStrokeManipulationHistory(
            Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>> stylusPointDictionary,
            Dictionary<string, Tuple<object, TransformGroup>> ElementsManipulationHistory)
        {
            Commit(new TimeMachineHistory(stylusPointDictionary, ElementsManipulationHistory, TimeMachineHistoryType.Manipulation));
        }

        public void CommitStrokeDrawingAttributesHistory(Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>> drawingAttributes)
        {
            Commit(new TimeMachineHistory(drawingAttributes, TimeMachineHistoryType.DrawingAttributes));
        }

        public void CommitStrokeEraseHistory(StrokeCollection stroke, StrokeCollection sourceStroke = null)
        {
            Commit(new TimeMachineHistory(stroke, TimeMachineHistoryType.Clear, true, sourceStroke));
        }

        public void CommitElementInsertHistory(UIElement element, bool strokeHasBeenCleared = false)
        {
            Commit(new TimeMachineHistory(element, TimeMachineHistoryType.ElementInsert, strokeHasBeenCleared));
        }

        internal void CommitCanvasClearHistory(StrokeCollection strokes, UIElement[] elements)
        {
            Commit(new TimeMachineHistory(strokes, TimeMachineHistoryType.Clear, true) { RemovedElements = elements });
        }

        public void ClearStrokeHistory() => ImportTimeMachineHistory(Array.Empty<TimeMachineHistory>());

        internal HistoryTransition Undo()
        {
            var item = _currentStrokeHistory[_currentIndex];
            _currentIndex--;
            NotifyUndoRedoState();
            return new HistoryTransition(item, HistoryDirection.Undo);
        }

        internal HistoryTransition Redo()
        {
            var item = _currentStrokeHistory[++_currentIndex];
            NotifyUndoRedoState();
            return new HistoryTransition(item, HistoryDirection.Redo);
        }

        internal Snapshot CaptureState() => new(_currentStrokeHistory.ToArray(), _currentIndex, _origin, _savedState);

        public bool ImportTimeMachineHistory(TimeMachineHistory[] sourceHistory)
        {
            RestoreState(new Snapshot(sourceHistory, sourceHistory.Length - 1, new object(), null));
            return true;
        }

        internal void RestoreState(Snapshot snapshot)
        {
            var previous = _currentStrokeHistory;
            int previousIndex = _currentIndex;
            object previousOrigin = _origin;
            object previousSavedState = _savedState;
            _currentStrokeHistory = new List<TimeMachineHistory>(snapshot.Entries);
            _currentIndex = snapshot.Index;
            _origin = snapshot.Origin;
            _savedState = snapshot.SavedState;
            try
            {
                NotifyUndoRedoState();
            }
            catch
            {
                _currentStrokeHistory = previous;
                _currentIndex = previousIndex;
                _origin = previousOrigin;
                _savedState = previousSavedState;
                throw;
            }
        }

        private void NotifyUndoRedoState()
        {
            OnUndoStateChanged?.Invoke(CanUndo);
            OnRedoStateChanged?.Invoke(CanRedo);
        }
    }

    public class TimeMachineHistory
    {
        public TimeMachineHistoryType CommitType;
        internal bool IsRemoval { get; }
        public StrokeCollection CurrentStroke;
        public StrokeCollection ReplacedStroke;
        public UIElement Element;
        internal UIElement[] RemovedElements;
        //这里说一下 Tuple 的 Value1 是初始值 ; Value 2 是改变值
        public Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>> StylusPointDictionary;
        public Dictionary<string, Tuple<object, TransformGroup>> ElementsManipulationHistory;
        public Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>> DrawingAttributes;
        // UserInput
        public TimeMachineHistory(StrokeCollection currentStroke, TimeMachineHistoryType commitType, bool strokeHasBeenCleared)
        {
            CommitType = commitType;
            CurrentStroke = currentStroke;
            IsRemoval = strokeHasBeenCleared;
            ReplacedStroke = null;
        }
        // Clear
        public TimeMachineHistory(StrokeCollection currentStroke, TimeMachineHistoryType commitType, bool strokeHasBeenCleared, StrokeCollection replacedStroke)
        {
            CommitType = commitType;
            CurrentStroke = currentStroke;
            IsRemoval = strokeHasBeenCleared;
            ReplacedStroke = replacedStroke;
        }
        // StrokeManipulation, ElementManipulation
        public TimeMachineHistory(
            Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>> stylusPointDictionary,
            Dictionary<string, Tuple<object, TransformGroup>> elementsManipulationHistory,
            TimeMachineHistoryType commitType)
        {
            CommitType = commitType;
            ElementsManipulationHistory = elementsManipulationHistory;
            StylusPointDictionary = stylusPointDictionary;
        }
        // trokeDrawingAttributes
        public TimeMachineHistory(Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>> drawingAttributes, TimeMachineHistoryType commitType)
        {
            CommitType = commitType;
            DrawingAttributes = drawingAttributes;
        }
        // Insert UIElement
        public TimeMachineHistory(UIElement element, TimeMachineHistoryType commitType, bool strokeHasBeenCleared)
        {
            CommitType = commitType;
            Element = element;
            IsRemoval = strokeHasBeenCleared;
        }

        internal void ApplyClear(InkCanvas canvas, bool remove)
        {
            var restored = remove ? ReplacedStroke : CurrentStroke;
            var removed = remove ? CurrentStroke : ReplacedStroke;
            if (restored != null)
            {
                foreach (var stroke in restored)
                    if (!canvas.Strokes.Contains(stroke)) canvas.Strokes.Add(stroke);
            }
            if (removed != null)
            {
                foreach (var stroke in removed)
                    if (canvas.Strokes.Contains(stroke)) canvas.Strokes.Remove(stroke);
            }
            if (RemovedElements == null) return;
            foreach (var element in RemovedElements)
            {
                if (remove) canvas.Children.Remove(element);
                else if (!canvas.Children.Contains(element)) canvas.Children.Add(element);
            }
        }
    }

    internal enum HistoryDirection { Undo, Redo }

    internal readonly record struct HistoryTransition(TimeMachineHistory Entry, HistoryDirection Direction)
    {
        internal bool RemoveOriginal => Direction == HistoryDirection.Undo ? !Entry.IsRemoval : Entry.IsRemoval;

        internal void ApplyClear(InkCanvas canvas) => Entry.ApplyClear(canvas, RemoveOriginal);
    }

    public enum TimeMachineHistoryType
    {
        UserInput,
        ShapeRecognition,
        Clear,
        Manipulation,
        DrawingAttributes,
        ElementInsert
    }
}
