using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentCanvas.Localization;
using System;

namespace FluentCanvas.ViewModels
{
    /// <summary>
    /// State for the PowerPoint navigation panels. The shell subscribes to the
    /// request events so it can run its own orchestration (screenshots, board
    /// mode switching) while the view stays free of shell references.
    /// </summary>
    public sealed partial class PptNavigationViewModel : ObservableObject
    {
        public PptNavigationViewModel()
        {
            PositionText = "0/0";
            IsBottomButtonVisible = true;
            IsSideButtonVisible = true;
        }

        [ObservableProperty]
        public partial int CurrentPosition { get; set; }

        [ObservableProperty]
        public partial int TotalSlides { get; set; }

        [ObservableProperty]
        public partial bool IsActive { get; set; }

        [ObservableProperty]
        public partial bool IsBottomPanelVisible { get; set; }

        [ObservableProperty]
        public partial bool IsSidePanelVisible { get; set; }

        [ObservableProperty]
        public partial bool IsBottomButtonVisible { get; set; }

        [ObservableProperty]
        public partial bool IsSideButtonVisible { get; set; }

        [ObservableProperty]
        public partial bool CanOpenSlideNavigation { get; set; } = true;

        public string PositionText { get; private set; }

        public string PositionDescription { get; private set; }

        public event EventHandler NextRequested;

        public event EventHandler PreviousRequested;

        public event EventHandler NavigationToggleRequested;

        public void SetPosition(int current, int total, int documentSlide)
        {
            CurrentPosition = current;
            TotalSlides = total;
            PositionText = total > 0 ? $"{current}/{total}" : current > 0 ? $"{current}" : "0/0";
            PositionDescription = UiText.Format("Presentation.PositionDescription", PositionText, documentSlide);
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(PositionDescription));
        }

        public void ShowPanels(bool bottom, bool side)
        {
            IsBottomPanelVisible = bottom;
            IsSidePanelVisible = side;
        }

        public void HidePanels()
        {
            IsBottomPanelVisible = false;
            IsSidePanelVisible = false;
        }

        [RelayCommand]
        private void Next() => NextRequested?.Invoke(this, EventArgs.Empty);

        [RelayCommand]
        private void Previous() => PreviousRequested?.Invoke(this, EventArgs.Empty);

        [RelayCommand]
        private void ToggleNavigation() => NavigationToggleRequested?.Invoke(this, EventArgs.Empty);
    }
}
