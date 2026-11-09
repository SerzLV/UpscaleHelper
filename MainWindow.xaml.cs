using System.ComponentModel;
using System.Windows;
using UpscaleHelper.Infrastructure;
using UpscaleHelper.Presentation;

namespace UpscaleHelper;

public partial class MainWindow : Window
{
    private readonly StudioViewModel studio;
    private readonly WindowPlacement placement = new(AppStorage.Root);

    private bool closingAfterCancellation;

    internal MainWindow(StudioViewModel studio)
    {
        this.studio = studio;
        InitializeComponent();
        placement.Restore(this);
        DataContext = studio;
        studio.PropertyChanged += OnStudioChanged;
    }

    private void OnStudioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudioViewModel.ActiveItem) && studio.ActiveItem is { } item)
            QueueList.ScrollIntoView(item);
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = studio.CanEdit && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) studio.AddFiles(paths);
        e.Handled = true;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (studio.RunningTask is { } running)
        {
            e.Cancel = true;
            if (closingAfterCancellation) return;
            closingAfterCancellation = true;
            studio.Cancel();
            await running;
            await Dispatcher.InvokeAsync(Close);
            return;
        }
        placement.Save(this);
        studio.StopPreview();
    }
}
