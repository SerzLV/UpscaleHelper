using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

public partial class PhotoCompressionWindow : Window
{
    private const int DefaultQuality = 95;
    private const int MinimumQuality = 1;
    private const int MaximumQuality = 100;

    private readonly string source;
    private readonly IPhotoCompressor compressor;
    private readonly ComparisonViewModel comparison;
    private readonly PhotoComparisonHistory history;
    private readonly CancellationTokenSource cancellation = new();
    private TemporaryFolder? temporary;
    private Task? job;
    private string? preview;
    private bool closing;

    internal PhotoCompressionWindow(string source, IPhotoCompressor compressor, IPreviewProvider previews, PhotoComparisonHistory history)
    {
        this.source = source;
        this.compressor = compressor;
        this.history = history;
        comparison = new ComparisonViewModel(previews);
        InitializeComponent();
        QualityBox.Text = DefaultQuality.ToString(CultureInfo.InvariantCulture);
        DataContext = comparison;
        SourceLabel.Text = Path.GetFileName(source);
        SourceLabel.ToolTip = source;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _ = comparison.LoadAsync(source, null);

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (QualityOptions is null || SaveButton is null) return;
        QualityOptions.Visibility = ModeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (preview is null) return;
        preview = null;
        SaveButton.IsEnabled = false;
        StatusText.Text = T("CompressionRegenerate");
    }

    private void OnQualityDown(object sender, RoutedEventArgs e) => StepQuality(-1);
    private void OnQualityUp(object sender, RoutedEventArgs e) => StepQuality(1);

    private void OnQualityKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || Keyboard.Modifiers != ModifierKeys.None) return;
        StepQuality(e.Key == Key.Up ? 1 : -1);
        e.Handled = true;
    }

    private void StepQuality(int step)
    {
        int current = ReadQuality(out int value) ? value : DefaultQuality;
        string next = Math.Clamp((long)current + step, MinimumQuality, MaximumQuality).ToString(CultureInfo.InvariantCulture);
        if (next != QualityBox.Text) QualityBox.Text = next;
    }

    private bool ReadQuality(out int value) =>
        int.TryParse(QualityBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        bool lossless = ModeBox.SelectedIndex == 0;
        int quality = DefaultQuality;
        if (!lossless && (!ReadQuality(out quality) || quality is < MinimumQuality or > MaximumQuality))
        {
            StatusText.Text = T("CompressionQualityRange");
            QualityBox.Focus();
            QualityBox.SelectAll();
            return;
        }
        preview = null;
        await RunAsync("CompressingPhoto", async token =>
        {
            temporary ??= new TemporaryFolder(Path.GetTempPath());
            string staged = Path.Combine(temporary.Path, "preview.webp");
            await Task.Run(() => compressor.CompressAsync(source, staged, lossless, quality, token), token);
            token.ThrowIfCancellationRequested();
            await comparison.LoadAsync(source, staged, resetZoom: false);
            token.ThrowIfCancellationRequested();
            if (comparison.After is null) throw new InvalidDataException(T("NoImageInFile"));

            preview = staged;
            StatusText.Text = (FileSize.TryRead(source), FileSize.TryRead(staged)) switch
            {
                (long before, long after) when before > 0 && after < before =>
                    T("CompressionSavedPercent", (before - after) / (double)before),
                (long before, long after) when after >= before => T("CompressionNotSmaller"),
                _ => T("CompressionPreviewReady")
            };
        });
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (preview is not { } staged || closing) return;
        var dialog = new SaveFileDialog
        {
            Title = T("CompressionSave"), Filter = "WebP (*.webp)|*.webp", DefaultExt = ".webp", OverwritePrompt = false,
            FileName = Path.GetFileNameWithoutExtension(source) + "_small.webp",
            InitialDirectory = Path.GetDirectoryName(source) ?? ""
        };
        if (dialog.ShowDialog(this) != true) return;
        string target = dialog.FileName;
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = T("DistinctFiles");
            return;
        }
        if (!string.Equals(Path.GetExtension(target), ".webp", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = T("CompressionWebpExtension");
            return;
        }
        if (File.Exists(target))
        {
            StatusText.Text = T("CompressionDestinationExists");
            return;
        }

        await RunAsync("CompressionSaving", async token =>
        {
            await Task.Run(() => SaveCopyAsync(staged, target, token), token);
            history.Remember(new(source, target, MediaKind.Image, null));
            StatusText.Text = T("CompressionSaved", Path.GetFileName(target));
        });
    }

    private static async Task SaveCopyAsync(string staged, string target, CancellationToken token)
    {
        using var folder = new TemporaryFolder(Path.GetDirectoryName(target)!);
        string pending = Path.Combine(folder.Path, "export.webp");
        await using (var input = File.OpenRead(staged))
        await using (var output = File.Create(pending))
            await input.CopyToAsync(output, token);
        token.ThrowIfCancellationRequested();
        File.Move(pending, target);
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> operation)
    {
        Options.IsEnabled = false;
        SaveButton.IsEnabled = false;
        StatusText.Text = T(status);
        job = RunOperationAsync();
        await job;
        job = null;
        Options.IsEnabled = !closing;
        SaveButton.IsEnabled = !closing && preview is not null;

        async Task RunOperationAsync()
        {
            try { await operation(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { StatusText.Text = T("CompressionError", ex.Message); }
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (job is not { IsCompleted: false } active) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        cancellation.Cancel();
        comparison.Cancel();
        await active;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        comparison.Cancel();
        cancellation.Dispose();
        temporary?.Dispose();
    }
}
