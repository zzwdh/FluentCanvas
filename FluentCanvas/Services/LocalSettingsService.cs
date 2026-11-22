using FluentCanvas.Helpers;
using System.IO;
using System.Threading;

namespace FluentCanvas.Services
{
    public class LocalSettingsService : ISettingsService
    {
        private SettingsStorage storage;
        private SettingsStorage Storage => LazyInitializer.EnsureInitialized(ref storage,
            () => new SettingsStorage(Path.Combine(StorageHelper.UserDataFolder, "Settings.json")));

        public Settings Model { get; set; } = new Settings();

        protected virtual string ReadLegacySettings() => null;

        internal Settings ReadStoredSettings() => Storage.Read(ReadLegacySettings);

        public bool Load()
        {
            var loaded = ReadStoredSettings();
            if (loaded == null)
                return false;
            loaded.Automation.AutoSavedStrokesLocation =
                NormalizeStorageLocation(loaded.Automation.AutoSavedStrokesLocation);
            Model = loaded;
            return true;
        }

        public void Save() => Storage.Write(Model);

        public string NormalizeStorageLocation(string location) => StorageHelper.NormalizeStorageLocation(location);
    }
}
