using System.Windows.Media.Imaging;

namespace UpscaleHelper.Presentation;

internal sealed record PreviewImage(BitmapSource Bitmap, int Width, int Height, long? FileSizeBytes)
{
    public string Dimensions => $"{Width} × {Height}";
    public bool HasNativePixels => Bitmap.PixelWidth == Width && Bitmap.PixelHeight == Height;
}
