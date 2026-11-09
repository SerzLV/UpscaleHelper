namespace UpscaleHelper.Infrastructure;

internal static class AppStorage
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpscaleHelper");
}
