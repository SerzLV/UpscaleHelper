using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using UpscaleHelper.Infrastructure;

namespace UpscaleHelper.Localization;

internal interface IInterfaceLanguage
{
    bool IsEnglish { get; }
    bool Select(bool english);
}

internal sealed class InterfaceLanguage : IInterfaceLanguage
{
    private readonly ResourceDictionary appResources;
    private readonly ISettingsStore settings;

    public InterfaceLanguage(ResourceDictionary appResources, ISettingsStore settings)
    {
        this.appResources = appResources;
        this.settings = settings;
        string? saved = settings.Current.Language;
        string? preference = saved is "ru" or "en" ? saved : ReadLegacyPreference();
        Apply(UseEnglish(preference, CultureInfo.CurrentUICulture));
        if (saved is not ("ru" or "en") && preference is "ru" or "en")
            settings.Update(value => value with { Language = preference });
    }

    public bool IsEnglish { get; private set; }

    public bool Select(bool english)
    {
        if (IsEnglish == english) return false;
        Apply(english);
        settings.Update(value => value with { Language = english ? "en" : "ru" });
        return true;
    }

    internal static bool UseEnglish(string? preference, CultureInfo culture) => preference switch
    {
        "en" => true,
        "ru" => false,
        _ => culture.TwoLetterISOLanguageName != "ru"
    };

    private static string? ReadLegacyPreference()
    {
        try { return File.ReadAllText(Path.Combine(AppStorage.Root, "language.txt")).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void Apply(bool english)
    {
        IsEnglish = english;
        Texts.UseLanguage(english);
        foreach (DictionaryEntry entry in Texts.ResourceSet)
            appResources[entry.Key] = entry.Value;
        appResources["UiLanguage"] = XmlLanguage.GetLanguage(Texts.Culture.IetfLanguageTag);
    }
}
