using FluentCanvas.Helpers;
using System.Windows.Controls;
using FluentCanvas.ViewModels;

namespace FluentCanvas.Views
{
    public partial class NotificationsView : UserControl
    {
        public NotificationsView()
        {
            InitializeComponent();
            DataContextChanged += (_, change) =>
            {
                if (change.OldValue is NotificationsViewModel previous)
                    previous.VisibilityChanged -= OnVisibilityChanged;
                if (change.NewValue is NotificationsViewModel current)
                    current.VisibilityChanged += OnVisibilityChanged;
            };
        }
        private void OnVisibilityChanged(object sender, bool visible)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => OnVisibilityChanged(sender, visible));
                return;
            }

            if (visible) AnimationsHelper.ShowWithSlideFromBottomAndFade(GridNotifications);
            else AnimationsHelper.HideWithSlideAndFade(GridNotifications);
        }
    }
}
