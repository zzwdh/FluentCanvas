using FluentCanvas.Helpers;
using System.Collections.Generic;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Diagnostics;
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
        private StrokeCollection AddedStroke;
        private readonly Dictionary<Stroke, (StylusPointCollection Before, StylusPointCollection After)> strokeManipulationHistory = new();
        private Stroke transformingBoardStroke;
        private Dictionary<Stroke, StylusPointCollection> StrokeInitialHistory = new Dictionary<Stroke, StylusPointCollection>();
        private Dictionary<string, Tuple<object, TransformGroup>> ElementsManipulationHistory;

        // object maybe TransformGroup or ElementData
        private Dictionary<string, object> ElementsInitialHistory = new Dictionary<string, object>();

        private Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>> DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
        private Dictionary<Guid, List<Stroke>> DrawingAttributesHistoryFlag = new Dictionary<Guid, List<Stroke>>()
        {
            { DrawingAttributeIds.Color, new List<Stroke>() },
            { DrawingAttributeIds.DrawingFlags, new List<Stroke>() },
            { DrawingAttributeIds.IsHighlighter, new List<Stroke>() },
            { DrawingAttributeIds.StylusHeight, new List<Stroke>() },
            { DrawingAttributeIds.StylusTip, new List<Stroke>() },
            { DrawingAttributeIds.StylusTipTransform, new List<Stroke>() },
            { DrawingAttributeIds.StylusWidth, new List<Stroke>() }
        };
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
            finally { _currentCommitType = previous; }
        }

        private void ReplayHistoryEntry(HistoryTransition transition)
        {
            var item = transition.Entry;
            bool removeOriginal = transition.RemoveOriginal;
            if (item.CommitType == TimeMachineHistoryType.UserInput)
            {
                if (!removeOriginal)
                {
                    foreach (var strokes in item.CurrentStroke)
                    {
                        if (!inkCanvas.Strokes.Contains(strokes))
                            inkCanvas.Strokes.Add(strokes);
                    }
                }
                else
                {
                    foreach (var strokes in item.CurrentStroke)
                    {
                        if (inkCanvas.Strokes.Contains(strokes))
                            inkCanvas.Strokes.Remove(strokes);
                    }
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.ShapeRecognition)
            {
                if (removeOriginal)
                {

                    foreach (var strokes in item.CurrentStroke)
                    {
                        if (inkCanvas.Strokes.Contains(strokes))
                            inkCanvas.Strokes.Remove(strokes);
                    }
                    foreach (var strokes in item.ReplacedStroke)
                    {
                        if (!inkCanvas.Strokes.Contains(strokes))
                            inkCanvas.Strokes.Add(strokes);
                    }
                }
                else
                {
                    foreach (var strokes in item.CurrentStroke)
                    {
                        if (!inkCanvas.Strokes.Contains(strokes))
                            inkCanvas.Strokes.Add(strokes);
                    }
                    foreach (var strokes in item.ReplacedStroke)
                    {
                        if (inkCanvas.Strokes.Contains(strokes))
                            inkCanvas.Strokes.Remove(strokes);
                    }
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.Manipulation)
            {
                if (!removeOriginal)
                {
                    if(item.StylusPointDictionary != null)
                    {
                        foreach (var currentStroke in item.StylusPointDictionary)
                        {
                            if (inkCanvas.Strokes.Contains(currentStroke.Key))
                            {
                                currentStroke.Key.StylusPoints = currentStroke.Value.Item2.Clone();
                            }
                        }
                    }
                    if(item.ElementsManipulationHistory != null)
                    {
                        foreach (var currentElement in item.ElementsManipulationHistory)
                        {
                            UIElement element = GetElementByTimestamp(inkCanvas, currentElement.Key);
                            if (element != null && inkCanvas.Children.Contains(element))
                            {
                                element.RenderTransform = currentElement.Value.Item2.Clone();
                            }
                            else
                            {
                                if (currentElement.Value.Item1 is FluentCanvasElementsHelper.ElementData)
                                {
                                    FluentCanvasElementsHelper.ElementData elementData = currentElement.Value.Item1 as FluentCanvasElementsHelper.ElementData;
                                    InkCanvas.SetLeft(elementData.FrameworkElement, elementData.SetLeftData);
                                    InkCanvas.SetTop(elementData.FrameworkElement, elementData.SetTopData);
                                    inkCanvas.Children.Add(elementData.FrameworkElement);
                                    elementData.FrameworkElement.RenderTransform = currentElement.Value.Item2.Clone();
                                }
                            }
                            ElementsInitialHistory.Remove(currentElement.Key);
                        }
                    }
                }
                else
                {
                    if(item.StylusPointDictionary != null)
                    {
                        foreach (var currentStroke in item.StylusPointDictionary)
                        {
                            if (inkCanvas.Strokes.Contains(currentStroke.Key))
                            {
                                currentStroke.Key.StylusPoints = currentStroke.Value.Item1.Clone();
                            }
                        }
                    }
                    if(item.ElementsManipulationHistory != null)
                    {
                        foreach (var currentElement in item.ElementsManipulationHistory)
                        {
                            UIElement element = GetElementByTimestamp(inkCanvas, currentElement.Key);
                            if (element != null && inkCanvas.Children.Contains(element))
                            {
                                if (currentElement.Value.Item1 is TransformGroup transformGroup)
                                {
                                    element.RenderTransform = transformGroup.Clone();
                                }
                                else if (currentElement.Value.Item1 is FluentCanvasElementsHelper.ElementData)
                                {
                                    inkCanvas.Children.Remove(element);
                                }
                            }
                            ElementsInitialHistory.Remove(currentElement.Key);
                        }
                    }
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.DrawingAttributes)
            {
                if (!removeOriginal)
                {
                    foreach (var currentStroke in item.DrawingAttributes)
                    {
                        if (inkCanvas.Strokes.Contains(currentStroke.Key))
                        {
                            currentStroke.Key.DrawingAttributes = currentStroke.Value.Item2.Clone();
                        }
                    }
                }
                else
                {
                    foreach (var currentStroke in item.DrawingAttributes)
                    {
                        if (inkCanvas.Strokes.Contains(currentStroke.Key))
                        {
                            currentStroke.Key.DrawingAttributes = currentStroke.Value.Item1.Clone();
                        }
                    }
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.Clear)
            {
                transition.ApplyClear(inkCanvas);
            }
            else if (item.CommitType == TimeMachineHistoryType.ElementInsert)
            {
                if (!removeOriginal)
                {
                    inkCanvas.Children.Add(item.Element);
                }
                else
                {
                    inkCanvas.Children.Remove(item.Element);
                }
            }
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
            canvasContentVersion++;
            if (e.Removed.Count > 0 && _currentCommitType != CommitReason.ShapeRecognition)
                InvalidateInkRecognition();
            if (!isHidingSubPanelsWhenInking)
            {
                isHidingSubPanelsWhenInking = true;
                HideSubPanels(); // 书写时自动隐藏二级菜单
            }

            foreach (var stroke in e?.Removed)
            {
                CaptureDeferredStrokePoints(stroke, stroke.StylusPoints);
                stroke.StylusPointsChanged -= Stroke_StylusPointsChanged;
                stroke.StylusPointsReplaced -= Stroke_StylusPointsReplaced;
                stroke.DrawingAttributesChanged -= Stroke_DrawingAttributesChanged;
                StrokeInitialHistory.Remove(stroke);
            }
            foreach (var stroke in e?.Added)
            {
                stroke.StylusPointsChanged += Stroke_StylusPointsChanged;
                stroke.StylusPointsReplaced += Stroke_StylusPointsReplaced;
                stroke.DrawingAttributesChanged += Stroke_DrawingAttributesChanged;
                StrokeInitialHistory[stroke] = stroke.StylusPoints.Clone();
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
            if (e.Added.Count > 0) timeMachine.CommitStrokeUserInputHistory(e.Added);
            else if (e.Removed.Count > 0) timeMachine.CommitStrokeEraseHistory(e.Removed);
        }

        private void Stroke_DrawingAttributesChanged(object sender, PropertyDataChangedEventArgs e)
        {
            canvasContentVersion++;
            if (_currentCommitType == CommitReason.CodeInput) return;
            var key = sender as Stroke;
            var currentValue = key.DrawingAttributes.Clone();
            DrawingAttributesHistory.TryGetValue(key, out var previousTuple);
            var previousValue = previousTuple?.Item1 ?? currentValue.Clone();
            var needUpdateValue = !DrawingAttributesHistoryFlag[e.PropertyGuid].Contains(key);
            if (needUpdateValue)
            {
                DrawingAttributesHistoryFlag[e.PropertyGuid].Add(key);
                Debug.Write(e.PreviousValue.ToString());
            }
            if (e.PropertyGuid == DrawingAttributeIds.Color && needUpdateValue)
            {
                previousValue.Color = (Color)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.IsHighlighter && needUpdateValue)
            {
                previousValue.IsHighlighter = (bool)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.StylusHeight && needUpdateValue)
            {
                previousValue.Height = (double)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.StylusWidth && needUpdateValue)
            {
                previousValue.Width = (double)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.StylusTip && needUpdateValue)
            {
                previousValue.StylusTip = (StylusTip)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.StylusTipTransform && needUpdateValue)
            {
                previousValue.StylusTipTransform = (Matrix)e.PreviousValue;
            }
            if (e.PropertyGuid == DrawingAttributeIds.DrawingFlags && needUpdateValue)
            {
                previousValue.IgnorePressure = (bool)e.PreviousValue;
            }
            DrawingAttributesHistory[key] = new Tuple<DrawingAttributes, DrawingAttributes>(previousValue, currentValue);
        }

        private void Stroke_StylusPointsReplaced(object sender, StylusPointsReplacedEventArgs e)
        {
            canvasContentVersion++;
            var stroke = (Stroke)sender;
            CaptureDeferredStrokePoints(stroke, e.PreviousStylusPoints);
            StrokeInitialHistory[stroke] = e.NewStylusPoints.Clone();
        }

        private void TransformStrokeDuringBoardGesture(Stroke stroke, Matrix transform)
        {
            var previous = transformingBoardStroke;
            if (dec.Count > 0) transformingBoardStroke = stroke;
            try { stroke.Transform(transform, false); }
            finally { transformingBoardStroke = previous; }
        }

        private void CaptureDeferredStrokePoints(Stroke stroke, StylusPointCollection points)
        {
            if (strokeManipulationHistory.TryGetValue(stroke, out var entry) && entry.After == null)
                strokeManipulationHistory[stroke] = (entry.Before, points.Clone());
        }

        private void Stroke_StylusPointsChanged(object sender, EventArgs e)
        {
            canvasContentVersion++;
            if (sender == null || _currentCommitType == CommitReason.CodeInput)
            {
                return;
            }
            var stroke = (Stroke)sender;
            // Board gesture deltas modify the same point collection in place. Snapshot the
            // final points at the gesture boundary, or before a removal or replacement.
            strokeManipulationHistory[stroke] = (StrokeInitialHistory[stroke],
                ReferenceEquals(stroke, transformingBoardStroke) ? null : stroke.StylusPoints.Clone());
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
            if (strokeManipulationHistory.Count == 0 && !(ElementsManipulationHistory?.Count > 0)) return;

            Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>> strokeSnapshots = null;
            if (strokeManipulationHistory.Count > 0)
            {
                strokeSnapshots = new(strokeManipulationHistory.Count);
                foreach (var (stroke, points) in strokeManipulationHistory)
                    strokeSnapshots.Add(stroke, Tuple.Create(points.Before, points.After ?? stroke.StylusPoints.Clone()));
            }
            timeMachine.CommitStrokeManipulationHistory(strokeSnapshots, ElementsManipulationHistory);
            if (strokeSnapshots != null)
            {
                foreach (var (stroke, points) in strokeSnapshots)
                    StrokeInitialHistory[stroke] = points.Item2;
                strokeManipulationHistory.Clear();
            }
            if (ElementsManipulationHistory != null)
            {
                foreach (var item in ElementsManipulationHistory)
                    ElementsInitialHistory[item.Key] = item.Value.Item2;
                ElementsManipulationHistory = null;
            }
        }

        private void CommitPendingStrokeErasure()
        {
            if (ReplacedStroke == null && AddedStroke == null) return;
            timeMachine.CommitStrokeEraseHistory(ReplacedStroke, AddedStroke);
            ReplacedStroke = null;
            AddedStroke = null;
        }

        private void CommitPendingCanvasEdits()
        {
            CommitPendingStrokeErasure();
            ToCommitStrokeManipulationHistoryAfterMouseUp();
            if (DrawingAttributesHistory.Count == 0) return;
            timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
            DrawingAttributesHistory = new();
            foreach (var flag in DrawingAttributesHistoryFlag.Values) flag.Clear();
        }
    }
}
