using System.Windows;

namespace UpscaleHelper.Presentation;

public partial class PhotoComparisonWindow : Window
{
    private readonly ComparisonViewModel comparison;
    private readonly FileComparison files;

    internal PhotoComparisonWindow(ComparisonViewModel comparison, FileComparison files)
    {
        this.comparison = comparison;
        this.files = files;
        InitializeComponent();
        DataContext = comparison;
        FileLabel.Text = files.Caption;
        FileLabel.ToolTip = $"{files.Original}\n{files.Enhanced}";
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        _ = comparison.LoadAsync(files.Original, files.Enhanced);

    private void OnClosed(object? sender, EventArgs e) => comparison.Cancel();
}
