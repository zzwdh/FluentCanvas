using Microsoft.UI.Dispatching;
using System;
using System.Threading;

namespace FluentCanvas;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--diagnostics")
        {
            App.Main();
            return;
        }

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(parameters =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new Views.DiagnosticsApp();
            });
        }
        catch (Exception ex)
        {
            Helpers.LogHelper.NewLog(ex.ToString());
            System.Windows.MessageBox.Show("无法打开诊断窗口。请检查应用安装是否完整。", "FluentCanvas");
            Environment.ExitCode = 1;
        }
    }
}
