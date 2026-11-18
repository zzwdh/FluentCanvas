using System;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace FluentCanvas
{
    public partial class MainWindow
    {
        private const string StartupTaskId = "FluentCanvasStartup";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        internal static async Task<StartupTaskState> GetStartupState(bool? enable = null)
        {
            if (App.IsPackaged)
            {
                var task = await StartupTask.GetAsync(StartupTaskId);
                if (enable == true && task.State == StartupTaskState.Disabled)
                    return await task.RequestEnableAsync();
                if (enable == false && task.State == StartupTaskState.Enabled)
                    task.Disable();
                return task.State;
            }

            const string name = "FluentCanvas";
            string command = $"\"{Environment.ProcessPath}\"";
            using var key = enable == true
                ? Registry.CurrentUser.CreateSubKey(RunKeyPath)
                : Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: enable == false);
            var existing = key?.GetValue(name) as string;
            bool owned = string.Equals(existing, command, StringComparison.OrdinalIgnoreCase);
            if (enable == true)
            {
                if (existing != null && !owned)
                    throw new InvalidOperationException("同名启动项属于其他安装位置。");
                key.SetValue(name, command);
                return StartupTaskState.Enabled;
            }
            if (enable == false && owned)
            {
                key.DeleteValue(name, false);
                return StartupTaskState.Disabled;
            }
            return owned ? StartupTaskState.Enabled : StartupTaskState.Disabled;
        }
    }
}
