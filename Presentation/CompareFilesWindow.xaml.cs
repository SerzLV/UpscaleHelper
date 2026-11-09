using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

internal sealed record FileComparison(string Original, string Enhanced, MediaKind Kind, VideoSegment? Segment)
{
    public string Caption => $"{Path.GetFileName(Original)} → {Path.GetFileName(Enhanced)}";
}

public partial class CompareFilesWindow : Window
{
    private string? original;
    private string? enhanced;

    internal CompareFilesWindow(FileComparison? initial)
    {
        InitializeComponent();
        if (initial is null) return;
        SetFile(true, initial.Original);
        SetFile(false, initial.Enhanced);
        ValidatePair();
    }

    internal FileComparison? Comparison { get; private set; }

    private void OnPickOriginal(object sender, RoutedEventArgs e) => PickFile(true);
    private void OnPickEnhanced(object sender, RoutedEventArgs e) => PickFile(false);

    private void PickFile(bool isOriginal)
    {
        string? directory = Path.GetDirectoryName((isOriginal ? original ?? enhanced : enhanced ?? original) ?? "");
        if (!isOriginal && enhanced is null && directory is not null)
        {
            string outputFolder = Path.Combine(directory, "Upscaled");
            if (Directory.Exists(outputFolder)) directory = outputFolder;
        }
        var dialog = new OpenFileDialog
        {
            Title = isOriginal ? T("PickOriginalTitle") : T("PickEnhancedTitle"),
            Filter = SupportedMedia.Filter,
            InitialDirectory = directory ?? ""
        };
        if (dialog.ShowDialog(this) != true) return;
        SetFile(isOriginal, dialog.FileName);
        ValidatePair();
    }

    private void SetFile(bool isOriginal, string path)
    {
        if (isOriginal) original = path;
        else enhanced = path;
        var label = isOriginal ? OriginalLabel : EnhancedLabel;
        label.Text = Path.GetFileName(path);
        label.ToolTip = path;
    }

    private bool ValidatePair()
    {
        var beforeKind = original is null ? null : SupportedMedia.GetKind(original);
        var afterKind = enhanced is null ? null : SupportedMedia.GetKind(enhanced);
        bool bothChosen = original is not null && enhanced is not null;
        string error = "";
        if ((original is not null && !File.Exists(original)) || (enhanced is not null && !File.Exists(enhanced)))
            error = T("ComparisonFileMissing");
        else if ((original is not null && beforeKind is null) || (enhanced is not null && afterKind is null))
            error = T("UnsupportedFile");
        else if (bothChosen && beforeKind != afterKind)
            error = T("MismatchedPair");
        else if (bothChosen && string.Equals(original, enhanced, StringComparison.OrdinalIgnoreCase))
            error = T("DistinctFiles");

        CompareButton.IsEnabled = bothChosen && error.Length == 0;
        VideoOptions.Visibility = CompareButton.IsEnabled && beforeKind == MediaKind.Video
            ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = error;
        return CompareButton.IsEnabled;
    }

    private void OnLengthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StartBox is not null) StartBox.IsEnabled = LengthBox.SelectedIndex > 0;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (!ValidatePair() || SupportedMedia.GetKind(original!) is not { } kind) return;
        VideoSegment? segment = null;
        if (kind == MediaKind.Video && LengthBox.SelectedIndex > 0)
        {
            if (!TimecodeInput.TryRead(StartBox.Text, out var start))
            {
                ErrorText.Text = T("InvalidTimecode");
                StartBox.Focus();
                return;
            }
            segment = new(start, TimeSpan.FromSeconds(LengthBox.SelectedIndex == 1 ? 5 : 10));
        }
        Comparison = new(original!, enhanced!, kind, segment);
        DialogResult = true;
    }
}
