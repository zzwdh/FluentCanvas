using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using FluentCanvas.Helpers;

namespace FluentCanvas.Localization
{
    internal static class UiText
    {
        internal static string Get(string key) => key;
    }
}

namespace FluentCanvas
{
    public partial class MainWindow
    {
        private readonly TextBlock TextBlockWhiteBoardIndexInfo = new(), BoardLeftPannelNextPageTextBlock = new();
        private readonly Button BoardLeftPannelNextPage1 = new(), BoardLeftPannelNextPage2 = new(),
            BtnWhiteBoardSwitchPrevious = new(), BtnBoardAddPage = new();
        private void ArchiveCanvasForTransition() { }
        private Task<bool> TryBackupBeforeClearAsync() => Task.FromResult(true);
        private IEnumerable<Stroke> EnumerateCommittedStrokes() => inkCanvas.Strokes;

        internal static void VerifyBoardPages()
        {
            var window = new MainWindow();
            try { window.CheckPageTransitions(); }
            finally { window.Close(); }
            Console.WriteLine("PASS Desktop and whiteboard retain page contents, history and indices across edits and failed transitions");
        }

        internal static void VerifyPresentationBackup()
        {
            var window = new MainWindow();
            try { window.CheckPresentationBackup(); }
            finally { window.Close(); }
            Console.WriteLine("PASS A slideshow changing behind the whiteboard preserves desktop, board and per-slide exit backups");
        }

        private void CheckPresentationBackup()
        {
            void WriteAt(int x) =>
                Canvas.Strokes.Add(new Stroke(new StylusPointCollection { new StylusPoint(x, x) }));
            var slides = new Dictionary<(string, int), CanvasPage>();
            WriteAt(10);
            var desktop = Pages.ReadPage(0);
            Pages.ReplaceDesktop(null);
            WriteAt(20);
            SwitchCanvasPage(1);
            WriteAt(30);
            slides[("deck", 1)] = Pages.ReadPage(0);
            Pages.ReplaceDesktop(null);
            if (Canvas.Strokes.Single().StylusPoints[0].X != 30)
                throw new InvalidOperationException("Changing slides behind the board replaced the visible page.");
            SwitchCanvasPage(0);
            WriteAt(40);
            slides[("deck", 2)] = Pages.ReadPage(0);

            var backup = CanvasSessionBackup.Capture(Pages, History.HasUnsavedChanges, desktop, slides);
            var expected = new Dictionary<string, int>
            {
                ["桌面批注.icart"] = 10, ["白板第 1 页.icart"] = 30,
                ["放映批注 1 - deck.icart"] = 20, ["放映批注 2 - deck.icart"] = 40
            };
            var directory = Directory.CreateTempSubdirectory("FluentCanvas-SessionBackup-");
            try
            {
                foreach (var (name, document) in backup)
                {
                    string path = Path.Combine(directory.FullName, name);
                    document.Save(path);
                    var saved = CanvasDocument.LoadSlideInk(path).Single().StylusPoints[0];
                    if (!expected.Remove(name, out int x) || Math.Abs(saved.X - x) > 0.1)
                        throw new InvalidOperationException($"Incorrect ink or unexpected backup for {name}.");
                }
                if (expected.Count != 0) throw new InvalidOperationException("Missing session backup.");
            }
            finally { directory.Delete(recursive: true); }

            SwitchCanvasPage(1);
            Pages.ReplaceDesktop(desktop);
            if (Canvas.Strokes.Single().StylusPoints[0].X != 30)
                throw new InvalidOperationException("Ending the show replaced the active whiteboard.");
            SwitchCanvasPage(0);
            if (Canvas.Strokes.Single().StylusPoints[0].X != 10)
                throw new InvalidOperationException("Returning to the desktop lost the pre-show ink.");
        }

        private void CheckPageTransitions()
        {
            static void Require(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
            }

            var desktop = new Stroke(new StylusPointCollection { new StylusPoint(1, 1) });
            var first = desktop.Clone();
            var last = desktop.Clone();
            Canvas.Strokes.Add(desktop);
            SwitchCanvasPage(1);
            Canvas.Strokes.Add(first);
            var element = new Image { Name = "BoardImage" };
            Canvas.Children.Add(element);
            History.MarkSaved();
            BtnWhiteBoardAdd_Click(null, null);
            Canvas.Strokes.Add(last);
            SwitchCanvasPage(1);
            Require(Canvas.Strokes.Single() == first && Canvas.Children[0] == element && !History.HasUnsavedChanges,
                "Switching back must restore the same strokes, attached media and saved history cursor.");
            BtnWhiteBoardAdd_Click(null, null);
            Require(BoardPage == 2 && BoardPageCount == 3 && !HasCanvasContent, "A middle insertion opens a blank page.");
            BtnWhiteBoardDelete_Click(null, null);
            Require(BoardPage == 2 && BoardPageCount == 2 && Canvas.Strokes.Single() == last && History.CanUndo,
                "Deleting a middle page must select its successor with the same history.");
            SwitchCanvasPage(0);
            Require(Canvas.Strokes.Single() == desktop && BoardPage == 2, "The desktop must keep the board position.");
            var savedDesktop = Pages.ReadPage(0);
            var otherCanvas = new InkCanvas();
            var invalidElement = new Button();
            otherCanvas.Children.Add(invalidElement);
            try
            {
                Pages.ReplaceDesktop(savedDesktop with { Elements = [invalidElement] });
                throw new Exception("Attaching an already parented element should have failed.");
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException) { }
            Require(Canvas.Strokes.Single() == desktop && ReferenceEquals(History.CurrentState, savedDesktop.History.Entries[0]),
                "A failed display must restore the original objects and undo cursor.");
            SwitchCanvasPage(2);
            BtnWhiteBoardDelete_Click(null, null);
            Require(BoardPageCount == 1 && BoardPage == 1 && Canvas.Strokes.Single() == first,
                "Deleting the final page in a multi-page board must select its predecessor.");
            BtnWhiteBoardDelete_Click(null, null);
            Undo();
            Require(BoardPageCount == 1 && Canvas.Children[0] == element && Canvas.Strokes.Single() == first,
                "Clearing the only page must remain undoable.");
        }
    }
}
