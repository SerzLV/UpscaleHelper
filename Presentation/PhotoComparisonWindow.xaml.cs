using System.Windows;
using System.Windows.Controls;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

public partial class PhotoComparisonWindow : Window
{
    private readonly ComparisonViewModel comparison;
    private readonly PhotoComparisonHistory history;
    private FileComparison files;
    private bool refreshingHistory;

    internal PhotoComparisonWindow(ComparisonViewModel comparison, FileComparison files, PhotoComparisonHistory history)
    {
        this.comparison = comparison;
        this.files = files;
        this.history = history;
        InitializeComponent();
        DataContext = comparison;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ShowPair(files);

    private void OnPickOriginal(object sender, RoutedEventArgs e) => ReplaceSide(true);
    private void OnPickEnhanced(object sender, RoutedEventArgs e) => ReplaceSide(false);

    private void ReplaceSide(bool original)
    {
        string current = original ? files.Original : files.Enhanced;
        if (StudioDialogs.PickPhoto(this, original ? T("PickOriginalTitle") : T("PickEnhancedTitle"), current) is not { } path) return;
        if (string.Equals(path, original ? files.Enhanced : files.Original, StringComparison.OrdinalIgnoreCase))
        {
            ErrorText.Text = T("DistinctFiles");
            return;
        }
        if (SupportedMedia.GetKind(path) != MediaKind.Image)
        {
            ErrorText.Text = T("UnsupportedFile");
            return;
        }
        ShowPair(original ? files with { Original = path } : files with { Enhanced = path });
    }

    private void OnRecentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingHistory || RecentPairs.SelectedItem is not FileComparison pair) return;
        if (File.Exists(pair.Original) && File.Exists(pair.Enhanced)) ShowPair(pair);
        else
        {
            RefreshHistory();
            ErrorText.Text = T("ComparisonFileMissing");
        }
    }

    private void ShowPair(FileComparison pair)
    {
        files = pair;
        OriginalLabel.Text = Path.GetFileName(pair.Original);
        OriginalLabel.ToolTip = pair.Original;
        EnhancedLabel.Text = Path.GetFileName(pair.Enhanced);
        EnhancedLabel.ToolTip = pair.Enhanced;
        ErrorText.Text = "";
        history.Remember(pair);
        RefreshHistory();
        _ = comparison.LoadAsync(pair.Original, pair.Enhanced);
    }

    private void RefreshHistory()
    {
        refreshingHistory = true;
        var entries = history.Available();
        RecentPairs.ItemsSource = entries;
        RecentPairs.SelectedItem = entries.FirstOrDefault(pair => pair == files);
        RecentPairs.IsEnabled = entries.Length > 0;
        refreshingHistory = false;
    }

    private void OnClosed(object? sender, EventArgs e) => comparison.Cancel();
}
