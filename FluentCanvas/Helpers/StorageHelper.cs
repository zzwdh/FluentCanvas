using System;
using System.IO;

namespace FluentCanvas.Helpers
{
    /// <summary>
    /// Resolves writable application folders.
    /// Packaged builds use the Windows App SDK / WinRT <c>ApplicationData.LocalFolder</c>,
    /// unpackaged builds use a plain per-user folder under LocalAppData.
    /// </summary>
    internal static class StorageHelper
    {
        private static string userDataFolder;

        internal static string UserDataPath => App.IsPackaged
            ? global::Windows.Storage.ApplicationData.Current.LocalFolder.Path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentCanvas");

        /// <summary>Folder for settings, logs and other mutable per-user state.</summary>
        public static string UserDataFolder
        {
            get
            {
                if (!string.IsNullOrEmpty(userDataFolder))
                {
                    return userDataFolder;
                }

                var folder = UserDataPath;
                Directory.CreateDirectory(folder);
                userDataFolder = folder;
                return userDataFolder;
            }
        }

        /// <summary>Default location for user-visible saves (screenshots, strokes).</summary>
        public static string GetDefaultStorageRoot()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(documents))
            {
                return Path.Combine(documents, "FluentCanvas");
            }

            return Path.Combine(UserDataFolder, "Storage");
        }

        public static bool IsDriveAvailable(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                return !string.IsNullOrEmpty(root) && Directory.Exists(root);
            }
            catch
            {
                return false;
            }
        }

        public static bool EnsureDirectory(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"StorageHelper | Could not create '{path}': {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// Returns the configured location when it is usable, otherwise a safe default.
        /// </summary>
        public static string NormalizeStorageLocation(string configured)
        {
            if (!string.IsNullOrEmpty(configured) && Path.IsPathFullyQualified(configured) &&
                IsDriveAvailable(configured) &&
                EnsureDirectory(configured))
            {
                return configured;
            }

            var fallback = GetDefaultStorageRoot();
            EnsureDirectory(fallback);
            return fallback;
        }
    }
}
