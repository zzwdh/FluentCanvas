using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;

namespace FluentCanvas.Localization;

internal sealed record UiLanguage(string Code, string Name);

internal static class UiText
{
    private static readonly ConcurrentDictionary<string, ResourceManager> Resources = new();

    internal static CultureInfo ActiveCulture { get; private set; } = CultureInfo.GetCultureInfo("zh-CN");

    private static readonly UiLanguage[] Supported =
    [
        new("zh-CN", "简体中文"),
        new("zh-TW", "繁體中文"),
        new("en", "English"),
        new("hi", "हिन्दी"),
        new("es", "Español"),
        new("ar", "العربية"),
        new("fr", "Français"),
        new("bn", "বাংলা"),
        new("pt", "Português"),
        new("ru", "Русский"),
        new("id", "Bahasa Indonesia"),
        new("ur", "اردو"),
        new("de", "Deutsch"),
        new("ja", "日本語"),
        new("mr", "मराठी"),
        new("vi", "Tiếng Việt"),
        new("te", "తెలుగు"),
        new("tr", "Türkçe"),
        new("ta", "தமிழ்"),
        new("ko", "한국어"),
        new("fa", "فارسی"),
        new("ha", "Hausa"),
        new("sw", "Kiswahili"),
        new("th", "ไทย")
    ];

    internal static IReadOnlyList<UiLanguage> Languages =>
        (UiLanguage[])[new("", Get("Canvas.FollowWindows")), .. Supported];

    internal static bool IsSupportedPreference(string code) =>
        code == "" || Supported.Any(language => language.Code == code);

    internal static void Initialize(string preference, CultureInfo windowsLanguage)
    {
        ActiveCulture = ResolveCulture(preference, windowsLanguage);
        CultureInfo.DefaultThreadCurrentUICulture = ActiveCulture;
        CultureInfo.CurrentUICulture = ActiveCulture;
    }

    internal static CultureInfo ResolveCulture(string preference, CultureInfo windowsLanguage)
    {
        if (!string.IsNullOrEmpty(preference) && IsSupportedPreference(preference))
            return CultureInfo.GetCultureInfo(preference);

        for (var culture = windowsLanguage; culture.Name.Length != 0; culture = culture.Parent)
        {
            if (culture.Name is "zh-Hant" or "zh-TW" or "zh-HK" or "zh-MO")
                return CultureInfo.GetCultureInfo("zh-TW");
            if (culture.Name is "zh-Hans" or "zh")
                return CultureInfo.GetCultureInfo("zh-CN");
            if (IsSupportedPreference(culture.Name))
                return culture;
        }

        return CultureInfo.GetCultureInfo("en");
    }

    public static string Get(string key)
    {
        var separator = key.IndexOf('.');
        var area = key[..separator];
        var manager = Resources.GetOrAdd(area, name =>
            new ResourceManager("FluentCanvas.Localization." + name, typeof(UiText).Assembly));
        return manager.GetString(key[(separator + 1)..], ActiveCulture)
            ?? throw new MissingManifestResourceException($"Missing UI text: {key}");
    }

    public static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
