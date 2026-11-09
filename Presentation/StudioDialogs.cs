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
    void ShowComparison(ComparisonClip clip, string name);
    void ShowError(string name, string error);
    void Reveal(string path);
}

internal sealed class StudioDialogs(IVideoFrameProvider frames) : IStudioDialogs
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
        var dialog = new CompareFilesWindow(initial) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Comparison : null;
    }

    public void ShowPhotoComparison(ComparisonViewModel comparison, FileComparison files) =>
        new PhotoComparisonWindow(comparison, files) { Owner = Application.Current.MainWindow }.ShowDialog();

    public void ShowError(string name, string error) =>
        new JobErrorWindow(name, error) { Owner = Application.Current.MainWindow }.ShowDialog();

    public void Reveal(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(T("MissingResult"), path);
        using var process = Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
