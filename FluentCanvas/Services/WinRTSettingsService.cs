namespace FluentCanvas.Services
{
    public sealed class WinRTSettingsService : LocalSettingsService
    {
        protected override string ReadLegacySettings()
        {
            var values = global::Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            return values.TryGetValue("SettingsJson", out var value) ? value as string : null;
        }
    }
}
