using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace FluentCanvas.Helpers
{
    /// <summary>
    /// Handles first-run migration and registration.
    /// The packaged (MSIX) build relies on the package manifest, while the
    /// unpackaged build keeps one per-user file association owner.
    /// </summary>
    internal static class InstallHelper
    {
        private const string ApplicationName = "FluentCanvas";
        private const string ClassesKeyPath = @"Software\Classes";
        private const string RegisteredApplicationsKeyPath = @"Software\RegisteredApplications";
        private const string CapabilitiesKeyPath = @"Software\FluentCanvas\Capabilities";
        private const string AppPathsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\FluentCanvas.exe";
        internal const string DefaultAppsSettings = "ms-settings:defaultapps?registeredAppUser=" + ApplicationName;

        private static readonly string[] MigratableFiles =
        {
            "Settings.json",
            "Names.txt",
            "Replace.txt"
        };

        private static readonly (string Extension, string ProgId, string Description)[] FileAssociations =
        {
            (".icart", "FluentCanvas.icart", "FluentCanvas Annotation File"),
            (".icstk", "FluentCanvas.icstk", "FluentCanvas Stroke File")
        };

        public static void RunStartupTasks()
        {
            try
            {
                MigrateLegacyUserData();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"InstallHelper | User data migration failed: {ex}", LogHelper.LogType.Error);
            }

            if (!App.IsPackaged)
            {
                try
                {
                    RegisterUnpackagedApp();
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"InstallHelper | Unpackaged registration failed: {ex}", LogHelper.LogType.Error);
                }
            }
        }

        private static void MigrateLegacyUserData()
        {
            var target = StorageHelper.UserDataFolder;
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            Directory.CreateDirectory(target);

            foreach (var source in GetLegacyDataDirectories())
            {
                if (!Directory.Exists(source) ||
                    string.Equals(
                        Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var fileName in MigratableFiles)
                {
                    var sourceFile = Path.Combine(source, fileName);
                    var targetFile = Path.Combine(target, fileName);
                    if (!File.Exists(sourceFile) || File.Exists(targetFile))
                    {
                        continue;
                    }

                    if (App.IsPackaged && fileName == "Settings.json" &&
                        global::Windows.Storage.ApplicationData.Current.LocalSettings.Values.ContainsKey("SettingsJson"))
                        continue;

                    try
                    {
                        File.Copy(sourceFile, targetFile, false);
                        LogHelper.WriteLogToFile($"InstallHelper | Migrated '{sourceFile}' to '{targetFile}'.", LogHelper.LogType.Event);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"InstallHelper | Could not migrate '{sourceFile}': {ex.Message}", LogHelper.LogType.Error);
                    }
                }
            }
        }

        private static IEnumerable<string> GetLegacyDataDirectories()
        {
            // Other InkCanvas installations can coexist with FluentCanvas.
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationName);

            var baseDirectory = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                yield return baseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            }
        }

        internal static bool IsAssociationOwner()
        {
            return GetRegistrationOwner() is string owner &&
                string.Equals(owner, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }

        internal static void RegisterUnpackagedApp(bool replaceOwner = false)
        {
            if (App.IsPackaged) return;
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || string.Equals(Path.GetFileName(exePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File associations require the FluentCanvas executable.");

            using var identity = WindowsIdentity.GetCurrent();
            using var registration = new Mutex(false, $@"Global\FluentCanvas.FileAssociations.{identity.User.Value}");
            try
            {
                if (!registration.WaitOne(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException("File association registration is already in progress.");
            }
            catch (AbandonedMutexException)
            {
                // The previous process left an incomplete registration. Repair the owned values below.
            }

            try
            {
                var owner = GetRegistrationOwner();
                if (!replaceOwner && owner != null && !string.Equals(owner, exePath, StringComparison.OrdinalIgnoreCase))
                    return;
                WriteRegistration(exePath);
            }
            finally
            {
                registration.ReleaseMutex();
            }
        }

        private static string GetRegistrationOwner()
        {
            using var appPath = Registry.CurrentUser.OpenSubKey(AppPathsKeyPath);
            if (appPath?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path)) return path;

            // Recognize a legacy registration interrupted before its App Paths value was written.
            foreach (var (_, progId, _) in FileAssociations)
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesKeyPath}\{progId}\shell\open\command");
                if (key?.GetValue(null) is string command && command.StartsWith('"'))
                {
                    int end = command.IndexOf('"', 1);
                    if (end > 1) return command[1..end];
                }
            }
            return null;
        }

        private static void WriteRegistration(string exePath)
        {
            using var appPath = Registry.CurrentUser.CreateSubKey(AppPathsKeyPath);
            bool changed = SetRegistrationValue(appPath, null, exePath);
            changed |= SetRegistrationValue(appPath, "Path", Path.GetDirectoryName(exePath));

            using var classes = Registry.CurrentUser.CreateSubKey(ClassesKeyPath);
            using var registeredApplications = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsKeyPath);
            using var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKeyPath);

            changed |= SetRegistrationValue(registeredApplications, ApplicationName, CapabilitiesKeyPath);
            changed |= SetRegistrationValue(capabilities, "ApplicationName", ApplicationName);
            changed |= SetRegistrationValue(capabilities, "ApplicationDescription", "FluentCanvas ink annotation board");

            using (var fileAssociations = capabilities.CreateSubKey("FileAssociations"))
            {
                foreach (var (extension, progId, description) in FileAssociations)
                {
                    changed |= SetRegistrationValue(fileAssociations, extension, progId);

                    using var progIdKey = classes.CreateSubKey(progId);
                    changed |= SetRegistrationValue(progIdKey, null, description);

                    using (var iconKey = progIdKey.CreateSubKey("DefaultIcon"))
                    {
                        changed |= SetRegistrationValue(iconKey, null, $"\"{exePath}\",0");
                    }

                    using (var commandKey = progIdKey.CreateSubKey(@"shell\open\command"))
                    {
                        changed |= SetRegistrationValue(commandKey, null, $"\"{exePath}\" \"%1\"");
                    }

                    using var extensionKey = classes.CreateSubKey(extension);
                    using var openWith = extensionKey.CreateSubKey("OpenWithProgids");
                    if (!Array.Exists(openWith.GetValueNames(), name => string.Equals(name, progId, StringComparison.OrdinalIgnoreCase)) ||
                        openWith.GetValueKind(progId) != RegistryValueKind.None)
                    {
                        openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
                        changed = true;
                    }
                }
            }

            if (!changed) return;
            unsafe { PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null); }
            LogHelper.WriteLogToFile("InstallHelper | Registered unpackaged file associations.", LogHelper.LogType.Event);
        }

        private static bool SetRegistrationValue(RegistryKey key, string name, string value)
        {
            if (key.GetValue(name) is string current && current == value) return false;
            key.SetValue(name, value);
            return true;
        }
    }
}
