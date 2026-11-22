using System.Windows;

namespace FluentCanvas
{
    public partial class MainWindow : Window
    {
        internal async void ShowNotificationAsync(string notice, bool isShowImmediately = true)
        {
            await notifications.ShowAsync(notice, isShowImmediately);
        }
    }
}
