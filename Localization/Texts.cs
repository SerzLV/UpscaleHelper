using System.Globalization;
using System.Resources;

namespace UpscaleHelper.Localization;

internal static class Texts
{
    private static readonly ResourceManager Resources = new("UpscaleHelper.Localization.Texts", typeof(Texts).Assembly);
    private static CultureInfo culture = CultureInfo.GetCultureInfo("ru-RU");

    public static CultureInfo Culture => Volatile.Read(ref culture);

    public static void UseLanguage(bool english) =>
        Volatile.Write(ref culture, CultureInfo.GetCultureInfo(english ? "en-US" : "ru-RU"));

    public static ResourceSet ResourceSet => Resources.GetResourceSet(Culture, true, true)!;

    public static string T(string key) => Resources.GetString(key, Culture) ?? key;

    public static string T(string key, params object[] args)
    {
        CultureInfo selected = Culture;
        return string.Format(selected, Resources.GetString(key, selected) ?? key, args);
    }
}
