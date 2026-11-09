namespace UpscaleHelper.Presentation;

internal static class FileSize
{
    private static readonly string[] Units = ["Bytes", "Kilobytes", "Megabytes", "Gigabytes", "Terabytes", "Petabytes"];

    public static long? TryRead(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static string Format(long bytes)
    {
        decimal amount = bytes;
        int unit = 0;
        while (amount >= 1000 && unit < Units.Length - 1)
        {
            amount /= 1000;
            unit++;
        }
        return $"{amount.ToString("0.##", Culture)} {T(Units[unit])}";
    }
}
