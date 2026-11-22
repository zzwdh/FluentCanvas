using Microsoft.Office.Interop.PowerPoint;
using System;

namespace FluentCanvas.Services
{
    public enum PresentationProvider
    {
        PowerPoint,
        Wps
    }

    public sealed record PowerPointSlide(
        string DeckKey, string Name, int SlideId, int SlideIndex, int ShowPosition, int SlideCount,
        nint WindowHandle = default, PresentationProvider Provider = PresentationProvider.PowerPoint,
        bool CanPersistInk = true, Guid SessionId = default);

    public interface ISlideShowService
    {
        PowerPointSlide CurrentSlide { get; }
        bool NextSlide();
        bool PreviousSlide();
        bool EndShow();
        bool ShowSlideNavigation();

        event Action<PowerPointSlide> SlideChanged;
        event Action SlideLeaving;
        event Action SlideShowEnded;
    }

    public interface IPowerPointService : ISlideShowService
    {
        void SetEnabled(bool enabled);
        bool IsCurrentPresentation(Presentation presentation);
        void GoToSlide(Presentation presentation, int slideIndex);
        string GetDeckKey(Presentation presentation);

        event Action<Presentation> PresentationAvailable;
    }
}
