using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

public partial class PhotoRestorationWindow : Window
{
    private readonly string source;
    private readonly IPhotoRestorer restorer;
    private readonly ComparisonViewModel comparison;
    private readonly PhotoComparisonHistory history;
    private TemporaryFolder? temporary;
    private CancellationTokenSource? cancellation;
    private Task? job;
    private string? preview;
    private bool closing;
    private bool drewDetails;
    private bool DrawsDetails => MethodChoice.SelectedIndex == 1;

    internal PhotoRestorationWindow(string source, IPhotoRestorer restorer, IPreviewProvider previews, PhotoComparisonHistory history,
        bool drawDetails = false)
    {
        this.source = source;
        this.restorer = restorer;
        this.history = history;
        comparison = new ComparisonViewModel(previews);
        InitializeComponent();
        DataContext = comparison;
        SourceLabel.Text = Path.GetFileName(source);
        SourceLabel.ToolTip = source;
        if (drawDetails) MethodChoice.SelectedIndex = 1;
        UpdateMethod();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        StatusText.Text = T(PreviewHint());
        _ = comparison.LoadAsync(source, null);
    }

    private void OnMethodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewButton is null || e.OriginalSource != MethodChoice) return;
        UpdateMethod();
        if (preview is not null) OnOptionsChanged(sender, e);
        else StatusText.Text = T(PreviewHint());
    }

    private void UpdateMethod()
    {
        PreviewButton.SetResourceReference(ContentControl.ContentProperty, DrawsDetails ? "DrawDetails" : "RestorationStart");
        CorrectionOption.Visibility = DrawsDetails ? Visibility.Collapsed : Visibility.Visible;
    }

    private string PreviewHint() => !restorer.IsInstalled ? "RestorationSetupNeeded" :
        DrawsDetails && !restorer.IsDrawDetailsInstalled ? "DrawDetailsSetupNeeded" : "RestorationStartHint";

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (preview is null) return;
        preview = null;
        comparison.ClearResult();
        UpdateActions(busy: false);
        string hint = PreviewHint();
        StatusText.Text = T(hint == "RestorationStartHint" ? "CompressionRegenerate" : hint);
    }

    private async void OnPreview(object sender, RoutedEventArgs e) =>
        await CreatePreviewAsync(DrawsDetails ? RestorationMode.DrawDetails : (RestorationMode)ModeBox.SelectedIndex);

    private async Task CreatePreviewAsync(RestorationMode mode)
    {
        if (job is not null || closing) return;
        if (!restorer.IsInstalled)
        {
            StatusText.Text = T("RestorationSetupNeeded");
            return;
        }
        bool drawing = mode == RestorationMode.DrawDetails;
        if (drawing && !restorer.IsDrawDetailsInstalled)
        {
            StatusText.Text = T("DrawDetailsSetupNeeded");
            return;
        }

        int strength = StrengthBox.SelectedIndex switch { 1 => 70, 2 => 100, _ => 45 };
        bool upscale = ScaleBox.SelectedIndex == 1;
        await RunAsync("RestoringPhoto", async token =>
        {
            temporary ??= new TemporaryFolder(Path.GetTempPath());
            string staged = Path.Combine(temporary.Path, drawing ? "drawn.png" : "restored.png");
            bool processing = true;
            var progress = new Progress<UpscaleProgress>(update =>
            {
                if (!processing || token.IsCancellationRequested) return;
                StatusText.Text = update.Stage;
                Progress.IsIndeterminate = update.Percent is null;
                Progress.Value = update.Percent ?? 0;
            });
            try { await Task.Run(() => restorer.RestoreAsync(source, staged, mode, strength, upscale, progress, token), token); }
            finally { processing = false; }

            token.ThrowIfCancellationRequested();
            StatusText.Text = T("LoadingPreview");
            await comparison.LoadAsync(source, staged, resetZoom: false);
            token.ThrowIfCancellationRequested();
            if (comparison.After is null) throw new InvalidDataException(T("NoImageInFile"));
            preview = staged;
            drewDetails = drawing;
            StatusText.Text = T(drawing ? "DrawDetailsReady" : "RestorationReady");
        });
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (preview is not { } staged || job is not null || closing) return;
        var dialog = new SaveFileDialog
        {
            Title = T("RestorationSave"), Filter = "PNG (*.png)|*.png", DefaultExt = ".png", OverwritePrompt = false,
            FileName = Path.GetFileNameWithoutExtension(source) + (drewDetails ? "_details.png" : "_restored.png"),
            InitialDirectory = Path.GetDirectoryName(source) ?? ""
        };
        if (dialog.ShowDialog(this) != true) return;
        string target = dialog.FileName;
        if (!string.Equals(Path.GetExtension(target), ".png", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = T("RestorationPngExtension");
            return;
        }
        if (File.Exists(target))
        {
            StatusText.Text = T("CompressionDestinationExists");
            return;
        }

        await RunAsync("CompressionSaving", async token =>
        {
            await Task.Run(() => PhotoCopy.SaveAsync(staged, target, token), token);
            history.Remember(new(source, target, MediaKind.Image, null));
            StatusText.Text = T("CompressionSaved", Path.GetFileName(target));
        });
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> operation)
    {
        using var request = new CancellationTokenSource();
        cancellation = request;
        UpdateActions(busy: true);
        CancelButton.IsEnabled = Progress.IsIndeterminate = true;
        StatusText.Text = T(status);
        job = RunOperationAsync();
        await job;
        job = null;
        cancellation = null;
        if (preview is null) comparison.ClearResult();
        UpdateActions(busy: false);

        async Task RunOperationAsync()
        {
            try { await operation(request.Token); }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { StatusText.Text = T("Canceled"); }
            catch (Exception ex) { StatusText.Text = T("RestorationError", ex.Message); }
        }
    }

    private void UpdateActions(bool busy)
    {
        MethodChoice.IsEnabled = AdvancedOptions.IsEnabled = !busy && !closing;
        SaveButton.IsEnabled = !busy && !closing && preview is not null;
        SaveButton.Visibility = SaveButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsDefault = SaveButton.IsEnabled;
        PreviewButton.Visibility = !busy && !closing && preview is null ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private void Cancel()
    {
        CancelButton.IsEnabled = false;
        cancellation?.Cancel();
        comparison.Cancel();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (job is not { IsCompleted: false } active) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        Cancel();
        await active;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        comparison.Cancel();
        temporary?.Dispose();
    }
}
