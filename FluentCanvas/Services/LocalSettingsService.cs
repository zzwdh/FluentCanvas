using FluentCanvas.Helpers;
using System.IO;

namespace FluentCanvas.Services
{
    public class LocalSettingsService : ISettingsService
    {
        private static string FilePath => Path.Combine(StorageHelper.UserDataFolder, "Settings.json");

        public Settings Model { get; set; } = new Settings();

        protected virtual string ReadLegacySettings() => null;

        public bool Load()
        {
            var loaded = SettingsStorage.Read(FilePath, ReadLegacySettings);
            if (loaded == null)
                return false;
            loaded.Automation.AutoSavedStrokesLocation =
                NormalizeStorageLocation(loaded.Automation.AutoSavedStrokesLocation);
            Model = loaded;
            return true;
        }

        public void Save() => SettingsStorage.Write(FilePath, Model);

        public string NormalizeStorageLocation(string location) => StorageHelper.NormalizeStorageLocation(location);
    }
}
