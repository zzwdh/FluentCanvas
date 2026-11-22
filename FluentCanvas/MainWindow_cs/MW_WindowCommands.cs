using System.Windows;

namespace FluentCanvas
{
    public partial class MainWindow
    {
        internal void ActivateCanvas()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
            if (!IsVisible) Show();
            Activate();
        }

        internal void ExitFromTray() => _ = FinishSessionAsync(restart: false, confirmTrayExit: true);

        internal void EndSessionFromSettings(bool restart) => _ = FinishSessionAsync(restart);
    }
}
