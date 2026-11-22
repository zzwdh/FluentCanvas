using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using FluentCanvas.Localization;

internal static class LocalizationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var regionalExamples = new Dictionary<string, string>
        {
            ["en-GB"] = "en", ["es-MX"] = "es", ["pt-BR"] = "pt", ["fr-CA"] = "fr",
            ["hi-IN"] = "hi", ["bn-BD"] = "bn", ["ur-PK"] = "ur", ["ar-EG"] = "ar",
            ["zh-SG"] = "zh-CN", ["zh-HK"] = "zh-TW", ["zh-MO"] = "zh-TW", ["fi-FI"] = "en"
        };
        foreach (var (region, expected) in regionalExamples)
        {
            var system = CultureInfo.GetCultureInfo(region);
            check(UiText.ResolveCulture("", system).Name == expected &&
                UiText.ResolveCulture("old-unknown-code", system).Name == expected, $"System language {region} resolves to {expected}");
        }

        var assembly = typeof(UiText).Assembly;
        const string prefix = "FluentCanvas.Localization.";
        var areas = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix) && name.EndsWith(".resources"))
            .Select(name => name[..^".resources".Length]).ToArray();
        check(new[] { "Canvas", "Settings", "Actions", "Presentation" }.All(area => areas.Contains(prefix + area)),
            "Compiled language resources include the canvas, settings, actions and presentations");
        var languages = UiText.Languages.Where(language => language.Code != "").ToArray();
        foreach (var language in languages)
        {
            UiText.Initialize(language.Code, CultureInfo.GetCultureInfo("en-US"));
            check(UiText.ActiveCulture.Name == language.Code && UiText.IsSupportedPreference(language.Code),
                $"Manual selection takes precedence for {language.Name}");
            foreach (string name in areas)
            {
                var manager = new ResourceManager(name, assembly);
                var source = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false);
                var culture = language.Code == "zh-CN" ? CultureInfo.InvariantCulture : UiText.ActiveCulture;
                var translated = manager.GetResourceSet(culture, true, false);
                if (translated == null || translated.Cast<DictionaryEntry>().Count() != source.Cast<DictionaryEntry>().Count())
                    throw new InvalidOperationException($"Incomplete resource set: {name} ({language.Code})");
                foreach (DictionaryEntry entry in source)
                {
                    var key = (string)entry.Key;
                    var value = translated.GetString(key);
                    if (string.IsNullOrWhiteSpace(value) ||
                        !Arguments(value).SequenceEqual(Arguments((string)entry.Value)) ||
                        UiText.Get(name[prefix.Length..] + "." + key) != value)
                        throw new InvalidOperationException($"Incorrect language resource: {name}.{key} ({language.Code})");
                }
            }
            check(true, $"Every resource and format argument resolves directly for {language.Name}");
        }
        check(!UiText.IsSupportedPreference("zz") && !UiText.IsSupportedPreference(null), "Reject unsupported preference writes");
    }

    private static IEnumerable<string> Arguments(string text)
    {
        CompositeFormat.Parse(text);
        return Regex.Matches(text, @"(?<!\{)\{\d+[^}]*\}").Select(match => match.Value).Order();
    }
}
