using System.Windows.Media.Imaging;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

internal sealed record PreviewImage(BitmapSource Bitmap, int Width, int Height, long? FileSizeBytes)
{
    public string Dimensions => $"{Width} × {Height}";
    public bool HasNativePixels => Bitmap.PixelWidth == Width && Bitmap.PixelHeight == Height;

    public static PreviewImage Decode(PreviewFrame preview, long? fileSize = null)
    {
        if (preview.Png.Length == 0) throw new InvalidDataException(T("NoImageInFile"));
        using var stream = new MemoryStream(preview.Png);
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        image.Freeze();
        return new(image, preview.NativeWidth > 0 ? preview.NativeWidth : image.PixelWidth,
            preview.NativeHeight > 0 ? preview.NativeHeight : image.PixelHeight, fileSize);
    }
}
