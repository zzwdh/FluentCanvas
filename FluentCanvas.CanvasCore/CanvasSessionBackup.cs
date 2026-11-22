using System.Collections.Generic;

namespace FluentCanvas.Helpers;

internal static class CanvasSessionBackup
{
    internal static List<(string Name, CanvasDocument.Snapshot Document)> Capture(CanvasPageBook pages,
        bool livePageDirty, CanvasPage desktopBeforeSlideShow,
        IReadOnlyDictionary<(string Deck, int Slide), CanvasPage> slides)
    {
        var result = new List<(string Name, CanvasDocument.Snapshot Document)>();
        void Add(string name, CanvasPage page) =>
            result.Add((name, CanvasDocument.Capture(page.Strokes, page.Elements)));

        for (int index = 0; index <= pages.WhiteboardCount; index++)
        {
            string name = index == 0 ? "桌面批注.icart" : $"白板第 {index} 页.icart";
            if (index == 0 && desktopBeforeSlideShow != null)
            {
                if (desktopBeforeSlideShow.History.HasUnsavedChanges) Add(name, desktopBeforeSlideShow);
            }
            else if (index == pages.ActiveIndex)
            {
                if (livePageDirty) Add(name, pages.ReadPage(index));
            }
            else if (pages.ReadPage(index) is { History.HasUnsavedChanges: true } stored)
                Add(name, stored);
        }

        // The desktop slot shows slide ink during a presentation. Preserve each edited
        // slide separately, including media that the presentation's ink writer omits.
        foreach (var (key, slide) in slides)
        {
            if (slide.History.HasUnsavedChanges) Add($"放映批注 {key.Slide} - {key.Deck}.icart", slide);
        }
        return result;
    }
}
