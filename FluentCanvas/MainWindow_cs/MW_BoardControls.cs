using FluentCanvas.Helpers;
using FluentCanvas.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Ink;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        private CanvasPageBook pageBook;
        private CanvasPageBook Pages => pageBook ??= new CanvasPageBook(CaptureCanvasPage, DisplayCanvasPage);
        private int CurrentWhiteboardIndex => Pages.WhiteboardIndex;
        private int WhiteboardTotalCount => Pages.WhiteboardCount;
        private int activeCanvasPage => Pages.ActiveIndex;
        private long canvasContextVersion;

        private IEnumerable<UIElement> EnumerateCanvasElements() =>
            inkCanvas.Children.Cast<UIElement>().Where(element => !MultiTouchInkPreview.IsTransient(element));

        private bool HasCanvasContent => EnumerateCommittedStrokes().Any() || EnumerateCanvasElements().Any();

        private void ClearStrokes(bool isErasedByCode)
        {
            if (!isErasedByCode) CommitPendingCanvasEdits();
            ResetCanvasContext();
            StrokeCollection removedStrokes = isErasedByCode ? null : new StrokeCollection(inkCanvas.Strokes);
            UIElement[] removedElements = isErasedByCode ? null : EnumerateCanvasElements().ToArray();
            var previous = _currentCommitType;
            _currentCommitType = CommitReason.CodeInput;
            try
            {
                inkCanvas.Strokes.Clear();
                inkCanvas.Children.Clear();
            }
            finally { _currentCommitType = previous; }
            if (!isErasedByCode && (removedStrokes.Count > 0 || removedElements.Length > 0))
                timeMachine.CommitCanvasClearHistory(removedStrokes, removedElements);
        }

        private void ResetCanvasContext()
        {
            canvasContextVersion++;
            CancelPendingCanvasInput();
            strokeManipulationHistory.Clear();
            ElementsManipulationHistory = null;
            ElementsInitialHistory.Clear();
            DrawingAttributesHistory.Clear();
            foreach (var flag in DrawingAttributesHistoryFlag.Values) flag.Clear();
            ReplacedStroke = null;
            AddedStroke = null;
        }

        private CanvasPage CaptureCanvasPage()
        {
            CommitPendingCanvasEdits();
            return new CanvasPage(new StrokeCollection(EnumerateCommittedStrokes()),
                EnumerateCanvasElements().ToArray(), timeMachine.CaptureState());
        }

        private void SwitchCanvasPage(int index) => Pages.SwitchTo(index);

        private void DisplayCanvasPage(CanvasPage page)
        {
            ClearStrokes(true);
            if (page == null)
            {
                timeMachine.ClearStrokeHistory();
                return;
            }

            var previous = _currentCommitType;
            _currentCommitType = CommitReason.CodeInput;
            try
            {
                if (page.Strokes.Count > 0) inkCanvas.Strokes.Add(page.Strokes);
                foreach (var element in page.Elements) inkCanvas.Children.Add(element);
                timeMachine.RestoreState(page.History);
            }
            finally { _currentCommitType = previous; }
        }

        private void BtnWhiteBoardSwitchPrevious_Click(object sender, EventArgs e) => ShowPreviousWhiteboard();

        private void ShowPreviousWhiteboard()
        {
            if (CurrentWhiteboardIndex <= 1) return;
            SwitchCanvasPage(CurrentWhiteboardIndex - 1);
            UpdateIndexInfoDisplay();
        }

        private void BtnWhiteBoardSwitchNext_Click(object sender, EventArgs e) => ShowNextWhiteboard();

        private void ShowNextWhiteboard()
        {
            if (CurrentWhiteboardIndex >= WhiteboardTotalCount)
            {
                AddWhiteboard();
                return;
            }
            ArchiveCanvasForTransition();
            SwitchCanvasPage(CurrentWhiteboardIndex + 1);
            UpdateIndexInfoDisplay();
        }

        private void BtnWhiteBoardAdd_Click(object sender, EventArgs e) => AddWhiteboard();

        private void AddWhiteboard()
        {
            if (!Pages.CanInsert) return;
            ArchiveCanvasForTransition();
            Pages.InsertWhiteboard();
            UpdateIndexInfoDisplay();
        }

        private async void BtnWhiteBoardDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!await TryBackupBeforeClearAsync()) return;
            if (!Pages.DeleteWhiteboard())
            {
                ClearStrokes(false);
                return;
            }
            UpdateIndexInfoDisplay();
        }

        private void UpdateIndexInfoDisplay()
        {
            TextBlockWhiteBoardIndexInfo.Text = $"{CurrentWhiteboardIndex} / {WhiteboardTotalCount}";
            bool onLastPage = CurrentWhiteboardIndex == WhiteboardTotalCount;
            BoardLeftPannelNextPage1.Width = onLastPage ? 26 : 0;
            BoardLeftPannelNextPage2.Width = onLastPage ? 0 : 26;
            BoardLeftPannelNextPageTextBlock.Text = UiText.Get(onLastPage ? "Canvas.AddPage" : "Canvas.NextPage");
            BtnWhiteBoardSwitchPrevious.IsEnabled = CurrentWhiteboardIndex > 1;
            BoardLeftPannelNextPage1.IsEnabled = !onLastPage || Pages.CanInsert;
            BtnBoardAddPage.IsEnabled = Pages.CanInsert;
        }
    }
}
