namespace UpscaleHelper.Processing;

internal static class OutputLimits
{
    public static void Validate(int width, int height, int scale, int nativeScale, MediaKind kind)
    {
        long outputWidth = (long)width * scale;
        long outputHeight = (long)height * scale;
        long nativeWidth = (long)width * nativeScale;
        long nativeHeight = (long)height * nativeScale;

        if (kind == MediaKind.Video && TooLarge(outputWidth, outputHeight, 8192, 36_000_000))
            throw new InvalidDataException(T("VideoSizeLimit", outputWidth, outputHeight));
        if (TooLarge(nativeWidth, nativeHeight, 16384, 140_000_000))
            throw new InvalidDataException(T("ModelSizeLimit", nativeWidth, nativeHeight));
    }

    private static bool TooLarge(long width, long height, int maxEdge, long maxPixels) =>
        width <= 0 || height <= 0 || width > maxEdge || height > maxEdge || width * height > maxPixels;
}
