using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

internal interface IStudioDialogs
{
    string[] PickFiles();
    string? PickFolder(string? current);
    VideoSegment? PickSegment(string name, VideoSegment? previous);
    FileComparison? PickComparisonFiles(FileComparison? initial);
    void ShowPhotoComparison(ComparisonViewModel comparison, FileComparison files);
    void ShowPhotoCompression(string? source);
    void ShowPhotoRestoration(string? source, bool drawDetails);
    void ShowComparison(ComparisonClip clip, string name);
    void ShowError(string name, string error);
    void Reveal(string path);
}

internal sealed class StudioDialogs(IVideoFrameProvider frames, IPreviewProvider previews,
    IPhotoCompressor compressor, IPhotoRestorer restorer, PhotoComparisonHistory history) : IStudioDialogs
{
    public string[] PickFiles()
    {
        var dialog = new OpenFileDialog { Title = T("AddFiles"), Filter = SupportedMedia.Filter, Multiselect = true };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string? current)
    {
        var dialog = new OpenFolderDialog { Title = T("OutputFolderDialog"), InitialDirectory = current ?? "" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public VideoSegment? PickSegment(string name, VideoSegment? previous)
    {
        var dialog = new SegmentWindow(name, previous) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Segment : null;
    }

    public void ShowComparison(ComparisonClip clip, string name) =>
        new VideoComparisonWindow(clip, name, frames) { Owner = Application.Current.MainWindow }.ShowDialog();

    public FileComparison? PickComparisonFiles(FileComparison? initial)
    {
        var dialog = new CompareFilesWindow(initial, history.Available()) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Comparison : null;
    }

    public void ShowPhotoComparison(ComparisonViewModel comparison, FileComparison files) =>
        new PhotoComparisonWindow(comparison, files, history, OpenRestoration, OpenCompression)
        { Owner = Application.Current.MainWindow }.ShowDialog();

    public void ShowPhotoCompression(string? source)
    {
        var owner = Application.Current.MainWindow;
        source ??= PickPhoto(owner, T("CompressPhoto"));
        if (source is null) return;
        OpenCompression(owner, source);
    }

    public void ShowPhotoRestoration(string? source, bool drawDetails)
    {
        var owner = Application.Current.MainWindow;
        source ??= PickPhoto(owner, T(drawDetails ? "DrawDetails" : "RestorationStart"));
        if (source is null) return;
        OpenRestoration(owner, source, drawDetails);
    }

    private void OpenCompression(Window owner, string source) =>
        new PhotoCompressionWindow(source, compressor, previews, history) { Owner = owner }.ShowDialog();

    private void OpenRestoration(Window owner, string source, bool drawDetails) =>
        new PhotoRestorationWindow(source, restorer, previews, history, drawDetails) { Owner = owner }.ShowDialog();

    internal static string? PickPhoto(Window owner, string title, string? current = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title, Filter = SupportedMedia.PhotoFilter,
            InitialDirectory = Path.GetDirectoryName(current) ?? ""
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public void ShowError(string name, string error) =>
        new JobErrorWindow(name, error) { Owner = Application.Current.MainWindow }.ShowDialog();

    public void Reveal(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(T("MissingResult"), path);
        using var process = Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
