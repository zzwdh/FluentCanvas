using FluentCanvas.Helpers;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace FluentCanvas
{
    public partial class MainWindow
    {
        internal bool ShellIsSlideShowActive => BtnPPTSlideShowEnd.Visibility == Visibility.Visible;

        internal bool ShellIsHostLoaded
        {
            get => isLoaded;
            set => isLoaded = value;
        }

        internal void ShellAnimateFloatingBarMargin() => ViewboxFloatingBarMarginAnimation();

        internal void ShellHideSubPanels(string mode) => HideSubPanels(mode);

        internal void ShellApplySystemTheme() => SystemEvents_UserPreferenceChanged(null, null);

        internal void ShellRefreshCursorMode() => inkCanvas_EditingModeChanged(inkCanvas, null);

        internal void ShellReloadSettings() => LoadSettings();

        internal int ShellBoundsWidth
        {
            get => BoundsWidth;
            set => BoundsWidth = value;
        }

        internal void ShellApplyScaling() => ApplyScaling();

        internal void ShellRestartAutoFoldTimer() => StartOrStoptimerCheckAutoFold();

        internal void ShellRestartKillProcessTimer()
        {
            if (!App.IsPackaged && (Settings.Automation.IsAutoKillEasiNote || Settings.Automation.IsAutoKillPptService))
            {
                timerKillProcess.Start();
            }
            else
            {
                timerKillProcess.Stop();
            }
        }

        internal void ShellShowNotification(string notice) => ShowNotificationAsync(notice);

        internal void ShellExitApplication()
        {
            CloseIsFromButton = true;
            Close();
        }

        internal void ShellRestartApplication()
        {
            try
            {
                var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
                ShellShowNotification($"重启未完成（{reason}）。请保存后手动重开应用。");
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"Restart failed: {ex}", LogHelper.LogType.Error);
                ShellShowNotification("重启失败。请保存后手动重开应用。");
            }
        }

        internal void ShellSetEdgeGestureUtil(bool enabled)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10))
            {
                EdgeGestureUtil.DisableEdgeGestures(new WindowInteropHelper(this).Handle, enabled);
            }
        }
    }
}
