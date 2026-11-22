using FluentCanvas.Helpers;
using System.Windows.Controls;
using FluentCanvas.ViewModels;
using System.ComponentModel;
using System.Windows.Input;

namespace FluentCanvas.Views
{
    public partial class PptNavigationView : UserControl
    {
        private PptNavigationViewModel ViewModel => (PptNavigationViewModel)DataContext;
        private object lastBorderMouseDownObject;

        public PptNavigationView()
        {
            InitializeComponent();

            DataContextChanged += (_, change) =>
            {
                if (change.OldValue is PptNavigationViewModel previous)
                    previous.PropertyChanged -= OnViewModelPropertyChanged;
                if (change.NewValue is PptNavigationViewModel current)
                    current.PropertyChanged += OnViewModelPropertyChanged;
            };
        }
        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PptNavigationViewModel.IsBottomPanelVisible))
            {
                if (ViewModel.IsBottomPanelVisible)
                {
                    AnimationsHelper.ShowWithSlideFromBottomAndFade(PPTNavigationBottomLeft);
                    AnimationsHelper.ShowWithSlideFromBottomAndFade(PPTNavigationBottomRight);
                }
                else
                {
                    AnimationsHelper.HideWithSlideAndFade(PPTNavigationBottomLeft);
                    AnimationsHelper.HideWithSlideAndFade(PPTNavigationBottomRight);
                }
            }
            else if (e.PropertyName == nameof(PptNavigationViewModel.IsSidePanelVisible))
            {
                if (ViewModel.IsSidePanelVisible)
                {
                    AnimationsHelper.ShowWithSlideFromLeftAndFade(PPTNavigationSidesLeft);
                    AnimationsHelper.ShowWithSlideFromRightAndFade(PPTNavigationSidesRight);
                }
                else
                {
                    AnimationsHelper.HideToLeft(PPTNavigationSidesLeft);
                    AnimationsHelper.HideToRight(PPTNavigationSidesRight);
                }
            }
        }

        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            lastBorderMouseDownObject = sender;
        }

        private void GridPPTControlPrevious_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;
            ViewModel.PreviousCommand.Execute(null);
        }

        private void GridPPTControlNext_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender) return;
            ViewModel.NextCommand.Execute(null);
        }

        private void PPTNavigationBtn_Click(object sender, MouseButtonEventArgs e)
        {
            if (lastBorderMouseDownObject != sender || !ViewModel.CanOpenSlideNavigation) return;
            ViewModel.ToggleNavigationCommand.Execute(null);
        }
    }
}
