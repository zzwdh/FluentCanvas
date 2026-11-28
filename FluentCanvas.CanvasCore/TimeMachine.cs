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
    public partial class TimeMachine
    {
        private NativeInkPage page = NativeInkPage.Create();
        private List<TimeMachineHistory> entries = new();

        private StrokeCollection projection;

        internal void BindInk(StrokeCollection strokes) => projection = strokes;

        internal bool CanUndo => page.State.Cursor > 0;
        internal bool CanRedo
        {
            get
            {
                var state = page.State;
                return state.Cursor < state.Count;
            }
        }

        internal ulong CurrentState => page.State.Revision;
        internal bool HasUnsavedChanges => page.State.Dirty;
        internal void MarkSaved() => page.MarkSaved();
        internal StrokeCollection MaterializeInk() => page.MaterializeInk();
        internal byte[] ExportInk() => page.ExportInk();

        // The snapshot has a native revision and the WPF objects used by element edits.
        internal sealed record Snapshot(TimeMachineHistory[] Entries, NativeInkPage Page)
        {
            internal bool HasUnsavedChanges => Page.State.Dirty;
            internal StrokeCollection MaterializeInk() => Page.MaterializeInk();
            internal byte[] ExportInk() => Page.ExportInk();
        }

        internal static Snapshot CreateBaseline(StrokeCollection strokes)
        {
            var native = NativeInkPage.Create();
            try
            {
                native.Apply(strokes.Select(NativeInkPage.Id).ToArray(), strokes, reset: true);
                return new([], native);
            }
            catch
            {
                native.Dispose();
                throw;
            }
        }

        internal bool FinishCollectedStroke(Stroke stroke, int style, double elapsedMilliseconds) =>
            page.FinishStroke(stroke, style, elapsedMilliseconds);

        public delegate void OnUndoStateChange(bool status);

        public event OnUndoStateChange OnUndoStateChanged;

        public delegate void OnRedoStateChange(bool status);

        public event OnRedoStateChange OnRedoStateChanged;

        private void Commit(TimeMachineHistory item, Action<NativeInkPage> nativeEdit = null)
        {
            int cursor = page.State.Cursor;
            entries.EnsureCapacity(cursor + 1);
            EditPage(page, item, projection, nativeEdit);
            if (cursor < entries.Count) entries.RemoveRange(cursor, entries.Count - cursor);
            entries.Add(item);
            NotifyUndoRedoState();
        }

        private static void EditPage(NativeInkPage target, TimeMachineHistory item, StrokeCollection visible, Action<NativeInkPage> custom = null)
        {
            SceneChange[] media = item.SceneChanges();
            if (media == null)
            {
                if (custom == null) ApplyEdit(target,item,visible);
                else custom(target);
            }
            else target.EditScene(media, () =>
            {
                if (custom == null) ApplyEdit(target,item,visible);
                else custom(target);
            });
        }

        private static void ApplyEdit(NativeInkPage target, TimeMachineHistory item, StrokeCollection visible)
        {
            switch (item.CommitType)
            {
                case TimeMachineHistoryType.UserInput:
                    var added = item.CurrentStroke;
                    if (visible != null && !EndsWith(visible, added))
                    {
                        target.ChangeLayers(visible, null, added);
                        return;
                    }
                    target.Append(added);
                    return;

                case TimeMachineHistoryType.DrawingAttributes:
                    target.UpdateRecords(item.ModifiedStrokes, item.CommitType == TimeMachineHistoryType.DrawingAttributes);
                    return;

                case TimeMachineHistoryType.Manipulation:
                    target.UpdateRecords(item.ModifiedStrokes, item.CommitType == TimeMachineHistoryType.DrawingAttributes);
                    return;

                case TimeMachineHistoryType.Clear:
                case TimeMachineHistoryType.ShapeRecognition:
                    bool erase = item.CommitType == TimeMachineHistoryType.Clear;
                    var removed = erase ? item.CurrentStroke : item.ReplacedStroke;
                    var inserted = erase ? item.ReplacedStroke : item.CurrentStroke;
                    if (visible == null && target.State.Count == 0 && removed?.Count > 0 && target.Position(NativeInkPage.Id(removed[0])) < 0)
                        target.Apply(removed.Select(NativeInkPage.Id).ToArray(), removed, reset: true);
                    target.ChangeLayers(visible, removed, inserted);
                    return;

                case TimeMachineHistoryType.ElementInsert:
                    target.Append(null);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(item));
            }
        }

        private static bool EndsWith(StrokeCollection visible, StrokeCollection added)
        {
            int offset = visible.Count - added.Count;
            if (offset < 0) return false;
            for (int i = 0; i < added.Count; i++)
                if (!ReferenceEquals(visible[offset + i], added[i])) return false;
            return true;
        }

        internal void ImportNativePage(NativeInkPage page, TimeMachineHistory[] sourceHistory)
        {
            // A failed WPF attach can retry with the same still-owned staging page.
            int first = page.State.Cursor;
            if (first > sourceHistory.Length) throw new ArgumentException("Imported history length mismatch.");
            for (int i = first; i < sourceHistory.Length; i++)
            {
                if (sourceHistory[i].CommitType != TimeMachineHistoryType.ElementInsert)
                    throw new ArgumentException("Missing imported ink transaction.");
                EditPage(page,sourceHistory[i],null);
            }
            page.MarkSaved();
            ReplaceState(page, new(sourceHistory));
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
            IEnumerable<Stroke> modifiedStrokes,
            Dictionary<string, Tuple<object, TransformGroup>> ElementsManipulationHistory)
        {
            Commit(new TimeMachineHistory(modifiedStrokes, ElementsManipulationHistory, TimeMachineHistoryType.Manipulation));
        }

        internal void CommitTransformedObjects(IEnumerable<Stroke> modifiedStrokes, GestureMediaChange[] changes)
        {
            Commit(new TimeMachineHistory(modifiedStrokes, null, TimeMachineHistoryType.Manipulation) { GestureMedia = changes });
        }

        public void CommitStrokeDrawingAttributesHistory(IEnumerable<Stroke> drawingAttributes)
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

        internal HistoryTransition Undo() => MoveCursor(false);
        internal HistoryTransition Redo() => MoveCursor(true);

        private HistoryTransition MoveCursor(bool forward)
        {
            int index = page.State.Cursor - (forward ? 0 : 1);
            var item = entries[index];
            if (forward) page.Redo();
            else page.Undo();
            try
            {
                var delta = item.CommitType == TimeMachineHistoryType.ElementInsert ? [] : page.Delta(forward);
                NotifyUndoRedoState();
                return new(item, forward ? HistoryDirection.Redo : HistoryDirection.Undo, page, delta);
            }
            catch
            {
                if (forward) page.Undo();
                else page.Redo();
                throw;
            }
        }

        internal Snapshot CaptureState() => new(entries.ToArray(), page.Copy());

        public bool ImportTimeMachineHistory(TimeMachineHistory[] sourceHistory)
        {
            var imported = NativeInkPage.Create();
            try
            {
                foreach (var item in sourceHistory) EditPage(imported, item, null);
                ReplaceState(imported, new(sourceHistory));
            }
            catch
            {
                imported.Dispose();
                throw;
            }
            return true;
        }

        internal void RestoreState(Snapshot snapshot)
        {
            var replacement = snapshot.Page.Copy();
            try { ReplaceState(replacement, new(snapshot.Entries)); }
            catch
            {
                replacement.Dispose();
                throw;
            }
        }

        private void ReplaceState(NativeInkPage replacement, List<TimeMachineHistory> history)
        {
            var previousPage = page;
            var previousEntries = entries;

            (page, entries) = (replacement, history);
            try { NotifyUndoRedoState(); }
            catch
            {
                (page, entries) = (previousPage, previousEntries);
                throw;
            }
            previousPage.Dispose();
        }

        private void NotifyUndoRedoState()
        {
            OnUndoStateChanged?.Invoke(CanUndo);
            OnRedoStateChanged?.Invoke(CanRedo);
        }
    }

    public partial class TimeMachineHistory
    {
        public TimeMachineHistoryType CommitType;
        internal bool IsRemoval { get; }
        public StrokeCollection CurrentStroke;
        public StrokeCollection ReplacedStroke;
        public UIElement Element;
        internal UIElement[] RemovedElements;
        // WPF retains render identities and the element part of mixed edits.
        internal Stroke[] ModifiedStrokes { get; }
        public Dictionary<string, Tuple<object, TransformGroup>> ElementsManipulationHistory;

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
            IEnumerable<Stroke> modifiedStrokes,
            Dictionary<string, Tuple<object, TransformGroup>> elementsManipulationHistory,
            TimeMachineHistoryType commitType)
        {
            CommitType = commitType;
            ElementsManipulationHistory = elementsManipulationHistory;
            ModifiedStrokes = modifiedStrokes?.ToArray();
        }
        // trokeDrawingAttributes
        public TimeMachineHistory(IEnumerable<Stroke> drawingAttributes, TimeMachineHistoryType commitType)
        {
            CommitType = commitType;
            ModifiedStrokes = drawingAttributes.ToArray();
        }
        // Insert UIElement
        public TimeMachineHistory(UIElement element, TimeMachineHistoryType commitType, bool strokeHasBeenCleared)
        {
            CommitType = commitType;
            Element = element;
            IsRemoval = strokeHasBeenCleared;
        }

        internal SceneChange[] SceneChanges()
        {
            if (GestureMedia is { Length: > 0 }) return GestureMedia.Select(SceneChange.Move).ToArray();
            if (RemovedElements is { Length: > 0 }) return RemovedElements.Select(SceneChange.Remove).ToArray();
            if (Element != null) return [IsRemoval ? SceneChange.Remove(Element) : SceneChange.Insert(Element)];
            return null;
        }
    }

    internal enum HistoryDirection { Undo, Redo }

    internal readonly record struct HistoryTransition(TimeMachineHistory Entry, HistoryDirection Direction, NativeInkPage Page, ulong[] Delta)
    {
        internal bool RemoveOriginal => Direction == HistoryDirection.Undo ? !Entry.IsRemoval : Entry.IsRemoval;

        internal void ApplyClear(InkCanvas canvas, bool reconcile = false)
        {
            ProjectInk(canvas, reconcile);
            ProjectElements(canvas);
        }

        internal void ProjectElements(InkCanvas canvas)
        {
            if (Entry.Element == null && Entry.RemovedElements is not { Length: > 0 } && Entry.GestureMedia is not { Length: > 0 }) return;
            var scene = Page.ReadScene();
            void Restore(UIElement element)
            {
                if (scene != null && scene.TryGetValue(SceneObject.ObjectId(element), out var state)) state.Attach(canvas,element);
                else canvas.Children.Remove(element);
            }
            if (Entry.Element != null) Restore(Entry.Element);
            if (Entry.RemovedElements != null) foreach (var element in Entry.RemovedElements) Restore(element);
            if (Entry.GestureMedia != null) foreach (var change in Entry.GestureMedia) Restore(change.Element);
        }

        internal void ProjectInk(InkCanvas canvas, bool reconcile = false)
        {
            if (Entry.CommitType == TimeMachineHistoryType.ElementInsert) return;
            var available = new Dictionary<ulong, Stroke>();
            if (Entry.CurrentStroke != null)
                foreach (var stroke in Entry.CurrentStroke) available[NativeInkPage.Id(stroke)] = stroke;
            if (Entry.ReplacedStroke != null)
                foreach (var stroke in Entry.ReplacedStroke) available[NativeInkPage.Id(stroke)] = stroke;
            if (reconcile)
                InkProjection.Restore(canvas.Strokes, Page, available, changed: null);
            else
                InkProjection.ApplyDelta(canvas.Strokes, Page, available, Delta);
        }
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
