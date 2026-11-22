using FluentCanvas.Helpers;
using Microsoft.Office.Core;
using Microsoft.Office.Interop.PowerPoint;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using PowerPointApplication = Microsoft.Office.Interop.PowerPoint.Application;

namespace FluentCanvas.Services
{
    public sealed class PowerPointService : IPowerPointService
    {
        private static readonly Guid PowerPointClassId = new("91493441-5a91-11cf-8700-00aa0060263b");
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan SlideInterval = TimeSpan.FromMilliseconds(200);
        private readonly Dispatcher dispatcher = System.Windows.Application.Current.Dispatcher;
        private readonly DispatcherTimer timer;
        private readonly ConditionalWeakTable<Presentation, string> unsavedDecks = new();
        private PowerPointApplication application;
        private Action removeSubscriptions;
        private uint officeProcessId;
        private Presentation editingPresentation;
        private ShowSession show;
        private bool enabled;
        private bool refreshing;
        private bool runningCommand;
        private bool refreshScheduled;
        private bool recheckShow;
        private bool hasActiveShows;

        public PowerPointService()
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = IdleInterval };
            timer.Tick += (_, _) => Refresh();
        }

        public PowerPointSlide CurrentSlide { get; private set; }
        public event Action<Presentation> PresentationAvailable;
        public event Action<PowerPointSlide> SlideChanged;
        public event Action SlideLeaving;
        public event Action SlideShowEnded;

        public void SetEnabled(bool enabled)
        {
            dispatcher.VerifyAccess();
            if (this.enabled == enabled) return;
            this.enabled = enabled;
            if (enabled)
            {
                timer.Start();
                return;
            }

            timer.Stop();
            Disconnect();
        }

        public string GetDeckKey(Presentation presentation)
        {
            if (string.IsNullOrWhiteSpace(presentation.Path))
                return unsavedDecks.GetValue(presentation, _ => "unsaved-" + Guid.NewGuid().ToString("N"));
            return PowerPointInkStorage.GetDeckKey(presentation.FullName);
        }

        public bool IsCurrentPresentation(Presentation presentation) =>
            enabled && presentation != null && !hasActiveShows && ReferenceEquals(editingPresentation, presentation);

        public void GoToSlide(Presentation presentation, int slideIndex)
        {
            if (!IsCurrentPresentation(presentation) || slideIndex < 1 || slideIndex > presentation.Slides.Count)
                return;
            var window = application.ActiveWindow;
            if (ReferenceEquals(window?.Presentation, presentation))
                window.View.GotoSlide(slideIndex);
        }

        public bool NextSlide() => RunOnShow(window => window.View.Next(), leavingSlide: true);
        public bool PreviousSlide() => RunOnShow(window => window.View.Previous(), leavingSlide: true);
        public bool EndShow() => RunOnShow(window => window.View.Exit(), leavingSlide: true);
        public bool ShowSlideNavigation() => RunOnShow(window =>
        {
            window.Activate();
            window.SlideNavigation.Visible = true;
        });

        private bool RunOnShow(Action<SlideShowWindow> action, bool leavingSlide = false)
        {
            dispatcher.VerifyAccess();
            var target = show;
            if (!enabled || refreshing || runningCommand || target == null || CurrentSlide == null)
                return false;

            runningCommand = true;
            try
            {
                // Reconcile a pending Office-side transition before capturing or issuing a command.
                if (!ReadCurrentSlide(target) || !enabled || !ReferenceEquals(show, target) || HasOtherForegroundShow(target))
                    return false;
                if (leavingSlide) SlideLeaving?.Invoke();
                if (!enabled || !ReferenceEquals(show, target) || !IsOfficeWindow(target.Handle))
                    return false;
                action(target.Window);
                return true;
            }
            catch (COMException ex)
            {
                LogHelper.WriteLogToFile($"PowerPoint command failed (0x{ex.HResult:X8}): {ex.Message}", LogHelper.LogType.Trace);
                return false;
            }
            finally
            {
                runningCommand = false;
                ScheduleRefresh();
            }
        }

        private void Refresh()
        {
            dispatcher.VerifyAccess();
            if (!enabled || refreshing || runningCommand) return;
            refreshing = true;
            try
            {
                if (application == null && !TryConnect()) return;

                if (show != null && (!IsOfficeWindow(show.Handle) || (recheckShow && !ContainsShow(show.Handle))))
                    ResetShow();
                recheckShow = false;

                if (show == null)
                {
                    var windows = application.SlideShowWindows;
                    int count = windows.Count;
                    hasActiveShows = count != 0;
                    if (count == 0)
                    {
                        RefreshEditingPresentation();
                        return;
                    }
                    var window = FindShow(windows, count);
                    if (window == null) return;

                    nint handle = window.HWND;
                    if (!IsOfficeWindow(handle)) return;
                    show = new ShowSession(window, handle, window.Presentation);
                    timer.Interval = SlideInterval;
                }

                ReadCurrentSlide(show);
            }
            catch (COMException ex) when (IsOfficeBusy(ex))
            {
                // Retry while keeping the displayed annotation session during modal dialogs.
            }
            catch (COMException ex)
            {
                LogHelper.WriteLogToFile($"PowerPoint connection failed (0x{ex.HResult:X8}): {ex.Message}", LogHelper.LogType.Trace);
                Disconnect();
            }
            finally { refreshing = false; }
        }

        private void RefreshEditingPresentation()
        {
            var presentation = application.Windows.Count > 0 ? application.ActivePresentation : null;
            if (ReferenceEquals(editingPresentation, presentation)) return;
            editingPresentation = presentation;
            if (presentation == null) return;

            // Dialogs may pump the dispatcher. Open them after the refresh has returned.
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsCurrentPresentation(presentation))
                    PresentationAvailable?.Invoke(presentation);
            }));
        }

        private static SlideShowWindow FindShow(SlideShowWindows windows, int count)
        {
            if (count == 1) return windows[1];

            // Keep an existing target until it closes. With multiple candidates, only
            // adopt a window the user has brought to the foreground.
            nint foreground = PInvoke.GetForegroundWindow();
            if (foreground == 0) return null;
            for (int index = 1; index <= count; index++)
            {
                var candidate = windows[index];
                if ((nint)candidate.HWND == foreground) return candidate;
            }
            return null;
        }

        private bool HasOtherForegroundShow(ShowSession target)
        {
            nint foreground = PInvoke.GetForegroundWindow();
            return foreground != target.Handle && IsOfficeWindow(foreground) && ContainsShow(foreground);
        }

        private bool ContainsShow(nint handle)
        {
            var windows = application.SlideShowWindows;
            int count = windows.Count;
            for (int index = 1; index <= count; index++)
            {
                if ((nint)windows[index].HWND == handle) return true;
            }
            return false;
        }

        private bool ReadCurrentSlide(ShowSession session)
        {
            if (!IsOfficeWindow(session.Handle)) return false;
            var view = session.Window.View;
            var slide = view.Slide;
            int position = view.CurrentShowPosition;
            if (slide == null || position <= 0) return false;
            if (ReferenceEquals(session.LastSlide, slide) && session.LastPosition == position && CurrentSlide != null)
                return true;

            var deck = slide.Parent as Presentation ?? session.Presentation;
            int slideId = slide.SlideID;
            if (slideId <= 0) return false;
            int index = slide.SlideIndex;
            var (showPosition, count) = ReadShowPosition(session, view, deck, index, position);
            var previous = CurrentSlide;
            bool sameDeck = previous != null && ReferenceEquals(session.LastDeck, deck);
            var next = new PowerPointSlide(
                sameDeck ? previous.DeckKey : GetDeckKey(deck), sameDeck ? previous.Name : deck.Name,
                slideId, index, showPosition, count, session.Handle, SessionId: session.Id);

            if (!enabled || !ReferenceEquals(show, session)) return false;
            session.LastSlide = slide;
            session.LastDeck = deck;
            session.LastPosition = position;
            if (next != previous)
            {
                CurrentSlide = next;
                SlideChanged?.Invoke(next);
            }
            return true;
        }

        private static (int Position, int Count) ReadShowPosition(
            ShowSession session, SlideShowView view, Presentation deck, int index, int position)
        {
            if (!ReferenceEquals(session.Presentation, deck)) return (position, 0);

            try
            {
                var settings = deck.SlideShowSettings;
                if (view.IsNamedShow == MsoTriState.msoTrue)
                {
                    int count = 0;
                    if (settings.NamedSlideShows[view.SlideShowName].SlideIDs is Array ids)
                    {
                        // Office can include an unused element at index zero.
                        foreach (object id in ids)
                        {
                            if (id is int slideId && slideId > 0) count++;
                        }
                    }
                    return (position, position <= count ? count : 0);
                }
                var range = settings.RangeType;
                if (range == PpSlideShowRangeType.ppShowSlideRange)
                {
                    int first = settings.StartingSlide;
                    int last = settings.EndingSlide;
                    if (first >= 1 && index >= first && index <= last)
                        return (index - first + 1, last - first + 1);
                    return (position, 0);
                }
                if (range == PpSlideShowRangeType.ppShowAll)
                {
                    int count = deck.Slides.Count;
                    return (position, position <= count ? count : 0);
                }
            }
            catch (COMException ex)
            {
                LogHelper.WriteLogToFile($"PowerPoint slide range unavailable (0x{ex.HResult:X8}).", LogHelper.LogType.Trace);
            }
            return (position, 0);
        }

        private unsafe bool TryConnect()
        {
            if (!PInvoke.GetActiveObject(PowerPointClassId, null, out object value).Succeeded ||
                value is not PowerPointApplication candidate)
                return false;
            uint processId = GetPowerPointProcessId(candidate.HWND);
            if (processId == 0) return false;

            // Bind callbacks to this particular connection attempt. Even if a busy Office
            // rejects event removal, callbacks from an abandoned attempt cannot affect state.
            Action unsubscribe = null;
            bool IsCurrent() => ReferenceEquals(removeSubscriptions, unsubscribe);
            void RefreshAfterEvent(bool checkWindow = false) => PostOfficeEvent(() =>
            {
                if (IsCurrent()) ScheduleRefresh(checkWindow);
            });

            EApplication_SlideShowBeginEventHandler begin = _ => PostOfficeEvent(() =>
            {
                if (!IsCurrent()) return;
                hasActiveShows = true;
                ScheduleRefresh();
            });
            EApplication_SlideShowNextSlideEventHandler next = window =>
            {
                if (dispatcher.CheckAccess() && IsCurrent() && !runningCommand && CurrentSlide != null &&
                    show is { } tracked && ReferenceEquals(tracked.Window, window))
                {
                    try
                    {
                        // Two external advances may arrive before the dispatcher has
                        // published the first new page. Skip a capture with a stale label.
                        if (ReferenceEquals(tracked.LastSlide, window.View.Slide))
                            SlideLeaving?.Invoke();
                    }
                    catch (COMException) { }
                }
                RefreshAfterEvent();
            };
            EApplication_SlideShowEndEventHandler end = _ => RefreshAfterEvent(checkWindow: true);
            EApplication_PresentationCloseEventHandler close = presentation => PostOfficeEvent(() =>
            {
                if (!IsCurrent()) return;
                if (ReferenceEquals(editingPresentation, presentation)) editingPresentation = null;
                ScheduleRefresh(checkWindow: true);
            });
            unsubscribe = () =>
            {
                TryUnsubscribe(() => candidate.SlideShowBegin -= begin);
                TryUnsubscribe(() => candidate.SlideShowNextSlide -= next);
                TryUnsubscribe(() => candidate.SlideShowEnd -= end);
                TryUnsubscribe(() => candidate.PresentationClose -= close);
            };

            try
            {
                candidate.SlideShowBegin += begin;
                candidate.SlideShowNextSlide += next;
                candidate.SlideShowEnd += end;
                candidate.PresentationClose += close;
            }
            catch (COMException)
            {
                unsubscribe();
                throw;
            }

            if (!enabled)
            {
                unsubscribe();
                return false;
            }
            application = candidate;
            officeProcessId = processId;
            removeSubscriptions = unsubscribe;
            return true;
        }

        private void PostOfficeEvent(Action eventAction)
        {
            if (dispatcher.CheckAccess())
            {
                if (enabled) eventAction();
                return;
            }
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (enabled) eventAction();
            }));
        }

        private void ScheduleRefresh(bool checkWindow = false)
        {
            recheckShow |= checkWindow;
            if (refreshScheduled) return;
            refreshScheduled = true;
            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                refreshScheduled = false;
                Refresh();
            }));
        }

        private void ResetShow()
        {
            show = null;
            timer.Interval = IdleInterval;
            if (CurrentSlide == null) return;
            CurrentSlide = null;
            SlideShowEnded?.Invoke();
        }

        private void Disconnect()
        {
            var unsubscribe = removeSubscriptions;
            removeSubscriptions = null;
            application = null;
            officeProcessId = 0;
            editingPresentation = null;
            recheckShow = false;
            hasActiveShows = false;
            unsubscribe?.Invoke();
            ResetShow();
        }

        private static void TryUnsubscribe(Action unsubscribe)
        {
            try { unsubscribe(); }
            catch (COMException) { }
        }

        private bool IsOfficeWindow(nint handle)
        {
            if (handle == 0 || officeProcessId == 0) return false;
            return PInvoke.GetWindowThreadProcessId(new HWND(handle), out uint owner) != 0 && owner == officeProcessId;
        }

        private static uint GetPowerPointProcessId(nint handle)
        {
            if (handle == 0 || PInvoke.GetWindowThreadProcessId(new HWND(handle), out uint processId) == 0)
                return 0;
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                return process.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase) ? processId : 0;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return 0;
            }
        }

        private static bool IsOfficeBusy(COMException ex) =>
            ex.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A);

        private sealed class ShowSession(SlideShowWindow window, nint handle, Presentation presentation)
        {
            internal SlideShowWindow Window { get; } = window;
            internal nint Handle { get; } = handle;
            internal Presentation Presentation { get; } = presentation;
            internal Guid Id { get; } = Guid.NewGuid();
            internal Slide LastSlide { get; set; }
            internal Presentation LastDeck { get; set; }
            internal int LastPosition { get; set; }
        }
    }
}
