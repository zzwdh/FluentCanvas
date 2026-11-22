namespace FluentCanvas.Services
{
    /// <summary>
    /// Owns the application settings model and its persistence.
    /// </summary>
    public interface ISettingsService
    {
        Settings Model { get; set; }

        /// <summary>Loads persisted settings. Returns false when no settings file exists.</summary>
        bool Load();

        void Save();

        string NormalizeStorageLocation(string location);
    }
}
