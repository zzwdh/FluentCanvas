using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace FluentCanvas.Helpers
{
    /// <summary>
    /// Handles first-run migration and registration.
    /// The packaged (MSIX) build relies on the package manifest, while the
    /// unpackaged build self-registers its file associations on first launch.
    /// </summary>
    internal static class InstallHelper
    {
        private const string ApplicationName = "FluentCanvas";
        private const string ClassesKeyPath = @"Software\Classes";
        private const string RegisteredApplicationsKeyPath = @"Software\RegisteredApplications";
        private const string CapabilitiesKeyPath = @"Software\FluentCanvas\Capabilities";
        private const string AppPathsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\FluentCanvas.exe";

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
            var target = App.RootPath;
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

        private static void RegisterUnpackagedApp()
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            using var classes = Registry.CurrentUser.CreateSubKey(ClassesKeyPath);
            using var registeredApplications = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsKeyPath);
            using var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKeyPath);

            registeredApplications.SetValue(ApplicationName, CapabilitiesKeyPath);
            capabilities.SetValue("ApplicationName", ApplicationName);
            capabilities.SetValue("ApplicationDescription", "FluentCanvas ink annotation board");

            using (var fileAssociations = capabilities.CreateSubKey("FileAssociations"))
            {
                foreach (var (extension, progId, description) in FileAssociations)
                {
                    fileAssociations.SetValue(extension, progId);

                    using var progIdKey = classes.CreateSubKey(progId);
                    progIdKey.SetValue(null, description);

                    using (var iconKey = progIdKey.CreateSubKey("DefaultIcon"))
                    {
                        iconKey.SetValue(null, $"\"{exePath}\",0");
                    }

                    using (var commandKey = progIdKey.CreateSubKey(@"shell\open\command"))
                    {
                        commandKey.SetValue(null, $"\"{exePath}\" \"%1\"");
                    }

                    using var extensionKey = classes.CreateSubKey(extension);
                    using var openWith = extensionKey.CreateSubKey("OpenWithProgids");
                    openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
                }
            }

            using (var appPath = Registry.CurrentUser.CreateSubKey(AppPathsKeyPath))
            {
                appPath.SetValue(null, exePath);
                appPath.SetValue("Path", Path.GetDirectoryName(exePath));
            }

            unsafe { PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null); }
            LogHelper.WriteLogToFile("InstallHelper | Registered unpackaged file associations.", LogHelper.LogType.Event);
        }

    }
}
