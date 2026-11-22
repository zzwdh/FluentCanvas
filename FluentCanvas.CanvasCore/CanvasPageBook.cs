using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;

namespace FluentCanvas.Helpers;

internal sealed record CanvasPage(StrokeCollection Strokes, UIElement[] Elements, TimeMachine.Snapshot History);

// Page zero is the desktop. The active page lives on the canvas; only inactive pages
// retain a snapshot so their strokes, media and undo entries keep the same identities.
internal sealed class CanvasPageBook(Func<CanvasPage> capture, Action<CanvasPage> display)
{
    internal const int MaximumWhiteboards = 99;
    private readonly List<CanvasPage> pages = new(MaximumWhiteboards + 1) { null, null };

    internal int ActiveIndex { get; private set; }
    internal int WhiteboardIndex { get; private set; } = 1;
    internal int WhiteboardCount => pages.Count - 1;
    internal bool CanInsert => WhiteboardCount < MaximumWhiteboards;

    internal CanvasPage ReadPage(int index) => index == ActiveIndex ? capture() : pages[index];

    internal void ReplaceDesktop(CanvasPage page)
    {
        if (ActiveIndex == 0) ChangeDisplay(page);
        else pages[0] = page;
    }

    internal void SwitchTo(int index)
    {
        if (ActiveIndex == index) return;
        var previous = ChangeDisplay(pages[index]);
        pages[ActiveIndex] = previous;
        pages[index] = null;
        ActiveIndex = index;
        if (index > 0) WhiteboardIndex = index;
    }

    internal void InsertWhiteboard()
    {
        if (!CanInsert) return;
        var previous = ChangeDisplay(null);
        pages[ActiveIndex] = previous;
        int index = WhiteboardIndex + 1;
        pages.Insert(index, null);
        ActiveIndex = WhiteboardIndex = index;
    }

    internal bool DeleteWhiteboard()
    {
        if (WhiteboardCount == 1) return false;
        int removed = WhiteboardIndex;
        int next = removed == WhiteboardCount ? removed - 1 : removed + 1;
        var previous = ChangeDisplay(pages[next]);
        if (ActiveIndex != removed) pages[ActiveIndex] = previous;
        pages[next] = null;
        pages.RemoveAt(removed);
        ActiveIndex = WhiteboardIndex = Math.Min(removed, WhiteboardCount);
        return true;
    }

    private CanvasPage ChangeDisplay(CanvasPage next)
    {
        var previous = capture();
        try { display(next); }
        catch
        {
            display(previous);
            throw;
        }
        return previous;
    }
}
