using System;
using System.Buffers.Binary;
using System.IO;
using FluentCanvas.Localization;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace FluentCanvas
{
    public partial class MainWindow
    {
        private const string StartupTaskId = "FluentCanvasStartup";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string StartupPolicyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        internal const string WindowsStartupSettings = "ms-settings:startupapps";

        internal static async Task<StartupTaskState?> GetStartupState(bool? enable = null)
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
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return null;

            using var policy = Registry.CurrentUser.OpenSubKey(StartupPolicyKeyPath);
            if (policy?.GetValue("DisableCurrentUserRun") is int value && value == 1)
                return StartupTaskState.DisabledByPolicy;

            string command = $"\"{path}\"";
            using var key = enable == true
                ? Registry.CurrentUser.CreateSubKey(RunKeyPath)
                : Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: enable == false);
            var existing = key?.GetValue(name);
            bool owned = string.Equals(existing as string, command, StringComparison.OrdinalIgnoreCase);
            if (enable == true)
            {
                if (!owned)
                {
                    if (existing != null && !IsMissingStartupExecutable(existing))
                        throw new InvalidOperationException(UiText.Get("Actions.StartupEntryInUse"));
                    key.SetValue(name, command);
                    owned = true;
                }
            }
            if (enable == false && owned)
            {
                key.DeleteValue(name, false);
                return StartupTaskState.Disabled;
            }
            if (!owned) return StartupTaskState.Disabled;

            // The unpackaged Run entry is managed by Windows Startup Apps. Treat its
            // approval record as a read-only compatibility check and defer unknown states to Settings.
            using var approvals = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath);
            return approvals?.GetValue(name) switch
            {
                null => StartupTaskState.Enabled,
                byte[] { Length: 12 } bytes => BinaryPrimitives.ReadUInt32LittleEndian(bytes) switch
                {
                    2 or 6 => StartupTaskState.Enabled,
                    3 or 7 => StartupTaskState.DisabledByUser,
                    _ => null,
                },
                _ => null,
            };
        }

        private static bool IsMissingStartupExecutable(object registration)
        {
            if (registration is not string command || command.Length < 3 || command[0] != '"' || command[^1] != '"')
                return false;
            string executable = command[1..^1];
            return !executable.Contains('"') && Path.IsPathFullyQualified(executable) &&
                string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(executable);
        }
    }
}
