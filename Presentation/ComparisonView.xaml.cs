using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace UpscaleHelper.Presentation;

public partial class ComparisonView : UserControl
{
    private readonly ScaleTransform scale = new(1, 1);
    private readonly TranslateTransform translation = new();
    private readonly RectangleGeometry wipeClip = new();
    private ComparisonViewModel? comparison;
    private Point? lastPanPoint;
    private Vector pan;
    private Size displayed;
    private double split = 0.5;
    private bool pixelMode;
    private bool updatingSlider;

    public ComparisonView()
    {
        InitializeComponent();
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(translation);
        OriginalImage.RenderTransform = EnhancedImage.RenderTransform = transform;
        DataContextChanged += OnComparisonChanged;
    }

    internal void ShowWipe()
    {
        if (WipeOption.IsEnabled) LayoutChoice.SelectedItem = WipeOption;
    }

    private void OnComparisonChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (comparison is not null)
        {
            comparison.PropertyChanged -= OnPreviewChanged;
            comparison.ResetRequested -= OnResetRequested;
        }
        comparison = e.NewValue as ComparisonViewModel;
        if (comparison is not null)
        {
            comparison.PropertyChanged += OnPreviewChanged;
            comparison.ResetRequested += OnResetRequested;
        }
        ResetZoom();
        ArrangeComparison();
    }

    private void OnResetRequested(object? sender, EventArgs e)
    {
        split = 0.5;
        ResetZoom();
    }

    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComparisonViewModel.Before) or nameof(ComparisonViewModel.After))
            ArrangeComparison();
    }

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e) => ArrangeComparison();
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateView();

    private void ArrangeComparison()
    {
        if (DividerLayer is null) return;
        if (comparison?.Before is null && comparison?.After is null)
            DimensionsLine.SetResourceReference(TextBlock.TextProperty, "LoadMedia");
        else
            DimensionsLine.Text = $"{comparison?.Before?.Dimensions ?? "—"}  →  {comparison?.After?.Dimensions ?? "—"}";
        WipeOption.IsEnabled = comparison is { Before: not null, After: not null };
        WipeOption.SetResourceReference(ToolTipProperty, WipeOption.IsEnabled ? "WipeTip" : "WipeUnavailable");
        bool wipe = WipeOption.IsEnabled && LayoutChoice.SelectedIndex == 1;
        Grid.SetColumnSpan(OriginalPane, wipe ? 3 : 1);
        Grid.SetColumn(EnhancedPane, wipe ? 0 : 2);
        Grid.SetColumnSpan(EnhancedPane, wipe ? 3 : 1);
        ResultTag.HorizontalAlignment = wipe ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        EnhancedPane.Clip = wipe ? wipeClip : null;
        DividerLayer.Visibility = wipe ? Visibility.Visible : Visibility.Collapsed;
        UpdateView();
    }

    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingSlider) return;
        pixelMode = false;
        UpdateView();
    }

    private void OnZoomReset(object sender, RoutedEventArgs e) => ResetZoom();

    private void ResetZoom()
    {
        pixelMode = false;
        pan = default;
        ZoomSlider.Value = 1;
        UpdateView();
    }

    private void OnPixelZoom(object sender, RoutedEventArgs e)
    {
        pixelMode = true;
        UpdateView();
    }

    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (!ZoomSlider.IsEnabled) return;
        pixelMode = false;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value * Math.Pow(1.25, e.Delta / 120.0),
            ZoomSlider.Minimum, ZoomSlider.Maximum);
        UpdateView();
        e.Handled = true;
    }

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (OriginalPane.Cursor != Cursors.ScrollAll || sender is not FrameworkElement pane || !pane.CaptureMouse()) return;
        lastPanPoint = e.GetPosition(pane);
        e.Handled = true;
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (lastPanPoint is not { } last || sender is not FrameworkElement { IsMouseCaptured: true } pane) return;
        Point point = e.GetPosition(pane);
        Vector delta = point - last;
        pan += new Vector(delta.X / Math.Max(1, displayed.Width), delta.Y / Math.Max(1, displayed.Height));
        lastPanPoint = point;
        UpdateView();
    }

    private void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseCaptured: true } pane) pane.ReleaseMouseCapture();
    }

    private void OnPanLost(object sender, MouseEventArgs e) => lastPanPoint = null;

    private void OnDividerDrag(object sender, DragDeltaEventArgs e) =>
        MoveDivider(e.HorizontalChange / Math.Max(1, Viewports.ActualWidth));

    private void OnDividerKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        MoveDivider(e.Key == Key.Left ? -0.025 : 0.025);
        e.Handled = true;
    }

    private void MoveDivider(double delta)
    {
        split = Math.Clamp(split + delta, 0.025, 0.975);
        UpdateDivider();
    }

    private void UpdateDivider()
    {
        if (DividerLayer.Visibility != Visibility.Visible) return;
        double x = split * Viewports.ActualWidth;
        wipeClip.Rect = new Rect(x, 0, Math.Max(0, Viewports.ActualWidth - x), Viewports.ActualHeight);
        Canvas.SetLeft(Divider, x - Divider.Width / 2);
        Divider.Height = Viewports.ActualHeight;
    }

    private void UpdateView()
    {
        if (DividerLayer is null) return;
        UpdateDivider();
        var reference = comparison?.After ?? comparison?.Before;
        bool hasImage = reference is not null;
        ZoomSlider.IsEnabled = ZoomReset.IsEnabled = hasImage;
        PixelZoom.IsEnabled = reference?.HasNativePixels == true;
        string tip = reference switch
        {
            null => "OpenMedia",
            { HasNativePixels: false } => "ReducedPreviewTip",
            _ when comparison?.After is null => "NativeOriginalTip",
            _ => "NativeResultTip"
        };
        PixelZoom.SetResourceReference(ToolTipProperty, tip);
        if (!PixelZoom.IsEnabled) pixelMode = false;

        double width = Math.Max(1, OriginalPane.ActualWidth - 2);
        double height = Math.Max(1, OriginalPane.ActualHeight - 2);
        double fit = reference is null ? 1 : Math.Min(width / reference.Bitmap.PixelWidth, height / reference.Bitmap.PixelHeight);
        double pixelZoom = PixelZoom.IsEnabled ? 1 / (fit * VisualTreeHelper.GetDpi(this).DpiScaleX) : 1;
        updatingSlider = true;
        ZoomSlider.Minimum = Math.Min(1, pixelZoom);
        ZoomSlider.Maximum = Math.Max(8, pixelZoom);
        if (pixelMode) ZoomSlider.Value = pixelZoom;
        updatingSlider = false;

        double zoom = ZoomSlider.Value;
        double shownWidth = (reference?.Bitmap.PixelWidth ?? 0) * fit * zoom;
        double shownHeight = (reference?.Bitmap.PixelHeight ?? 0) * fit * zoom;
        displayed = new Size(shownWidth, shownHeight);
        double limitX = PanLimit(shownWidth, width);
        double limitY = PanLimit(shownHeight, height);
        pan = new Vector(Math.Clamp(pan.X, -limitX, limitX), Math.Clamp(pan.Y, -limitY, limitY));
        scale.ScaleX = scale.ScaleY = zoom;
        translation.X = pan.X * shownWidth;
        translation.Y = pan.Y * shownHeight;
        OriginalPane.Cursor = EnhancedPane.Cursor = limitX > 0 || limitY > 0 ? Cursors.ScrollAll : Cursors.Arrow;
        if (pixelMode) ZoomReset.SetResourceReference(ContentControl.ContentProperty, "Fit");
        else ZoomReset.Content = $"{zoom:P0}";
        var sampling = pixelMode ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        RenderOptions.SetBitmapScalingMode(OriginalImage, sampling);
        RenderOptions.SetBitmapScalingMode(EnhancedImage, sampling);
    }

    private static double PanLimit(double shown, double available) => shown > available ? (shown - available) / (2 * shown) : 0;
}
