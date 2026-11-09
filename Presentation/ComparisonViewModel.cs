using System.Windows.Media.Imaging;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

internal sealed class ComparisonViewModel(IPreviewProvider previews) : ObservableModel
{
    private CancellationTokenSource? previewLoad;
    private PreviewImage? before;
    private PreviewImage? after;
    private string beforeHint = "SelectQueueFile";
    private string afterHint = "ResultPlaceholder";

    public PreviewImage? Before
    {
        get => before;
        private set { if (Set(ref before, value)) Notify(nameof(FileSizesLabel)); }
    }
    public PreviewImage? After
    {
        get => after;
        private set { if (Set(ref after, value)) Notify(nameof(FileSizesLabel)); }
    }
    public string FileSizesLabel =>
        Before?.FileSizeBytes is null && After?.FileSizeBytes is null ? "" :
        T("FileSizePair",
            Before?.FileSizeBytes is { } source ? FileSize.Format(source) : "—",
            After?.FileSizeBytes is { } result ? FileSize.Format(result) : "—");
    public string BeforeHint => T(beforeHint);
    public string AfterHint => T(afterHint);
    public event EventHandler? ResetRequested;

    public void Cancel() => previewLoad?.Cancel();

    public void RefreshLanguage()
    {
        Notify(nameof(BeforeHint));
        Notify(nameof(AfterHint));
        Notify(nameof(FileSizesLabel));
    }

    public async Task LoadAsync(string? source, string? result, bool resetZoom = true)
    {
        Cancel();
        if (resetZoom) ResetRequested?.Invoke(this, EventArgs.Empty);
        Before = null;
        After = null;
        Set(ref beforeHint, source is null ? "SelectQueueFile" : "LoadingPreview", nameof(BeforeHint));
        Set(ref afterHint, result is null ? "ResultPlaceholder" : "LoadingPreview", nameof(AfterHint));
        if (source is null) return;

        using var load = new CancellationTokenSource();
        previewLoad = load;
        try
        {
            var original = LoadSideAsync(source, (bitmap, hint) =>
            {
                Before = bitmap;
                Set(ref beforeHint, hint, nameof(BeforeHint));
            }, load.Token);
            if (result is not null)
            {
                await Task.WhenAll(original, LoadSideAsync(result, (bitmap, hint) =>
                {
                    After = bitmap;
                    Set(ref afterHint, hint, nameof(AfterHint));
                }, load.Token));
            }
            else await original;
        }
        finally
        {
            if (previewLoad == load) previewLoad = null;
        }
    }

    private async Task LoadSideAsync(string path, Action<PreviewImage?, string> apply, CancellationToken token)
    {
        try
        {
            var bitmap = await Task.Run(async () =>
            {
                var preview = await previews.ReadAsync(path, token);
                token.ThrowIfCancellationRequested();
                if (preview.Png.Length == 0) throw new InvalidDataException(T("NoImageInFile"));
                using var stream = new MemoryStream(preview.Png);
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                image.Freeze();
                return new PreviewImage(image, preview.NativeWidth > 0 ? preview.NativeWidth : image.PixelWidth,
                    preview.NativeHeight > 0 ? preview.NativeHeight : image.PixelHeight, FileSize.TryRead(path));
            }, token);
            if (!token.IsCancellationRequested) apply(bitmap, "");
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested) apply(null, "CannotOpenFile");
        }
    }
}
