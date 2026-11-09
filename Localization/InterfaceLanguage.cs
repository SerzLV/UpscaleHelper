using System.Collections;
using System.Windows;
using System.Windows.Markup;

namespace UpscaleHelper.Localization;

internal sealed class InterfaceLanguage
{
    private readonly ResourceDictionary appResources;
    private readonly string preferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpscaleHelper", "language.txt");
    private ResourceDictionary? dictionary;

    public InterfaceLanguage(ResourceDictionary appResources)
    {
        this.appResources = appResources;
        Apply(ReadPreference());
    }

    public bool IsEnglish { get; private set; }

    public bool Select(bool english)
    {
        if (IsEnglish == english) return false;
        Apply(english);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
            File.WriteAllText(preferencePath, english ? "en" : "ru");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return true;
    }

    private bool ReadPreference()
    {
        try { return File.ReadAllText(preferencePath).Trim() == "en"; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void Apply(bool english)
    {
        IsEnglish = english;
        Texts.UseLanguage(english);
        var resources = new ResourceDictionary();
        foreach (DictionaryEntry entry in Texts.ResourceSet)
            resources.Add(entry.Key, entry.Value);
        resources.Add("UiLanguage", XmlLanguage.GetLanguage(Texts.Culture.IetfLanguageTag));
        var merged = appResources.MergedDictionaries;
        if (dictionary is null) merged.Add(resources);
        else merged[merged.IndexOf(dictionary)] = resources;
        dictionary = resources;
    }
}
