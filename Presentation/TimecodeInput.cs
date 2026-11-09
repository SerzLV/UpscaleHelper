using System.Globalization;

namespace UpscaleHelper.Presentation;

internal static class TimecodeInput
{
    public static bool TryRead(string text, out TimeSpan start)
    {
        start = TimeSpan.Zero;
        string[] fields = text.Trim().Split(':');
        if (fields.Length > 3) return false;
        long seconds = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            if (!int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
                (i > 0 && value >= 60))
                return false;
            seconds = seconds * 60 + value;
        }
        if (seconds >= TimeSpan.MaxValue.TotalSeconds) return false;
        start = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
