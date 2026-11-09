using System.Windows;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

public partial class SegmentWindow : Window
{
    internal SegmentWindow(string name, VideoSegment? previous)
    {
        InitializeComponent();
        FileLabel.Text = name;
        var start = previous?.Start ?? TimeSpan.Zero;
        StartBox.Text = start.TotalHours >= 1 ? $"{(int)start.TotalHours}:{start.Minutes:00}:{start.Seconds:00}"
            : $"{start.Minutes}:{start.Seconds:00}";
        LengthBox.SelectedIndex = previous?.Length.TotalSeconds == 10 ? 1 : 0;
        StartBox.SelectAll();
    }

    public VideoSegment? Segment { get; private set; }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (!TimecodeInput.TryRead(StartBox.Text, out var start))
        {
            ErrorText.Text = T("InvalidTimecode");
            StartBox.Focus();
            return;
        }
        Segment = new(start, TimeSpan.FromSeconds(LengthBox.SelectedIndex == 1 ? 10 : 5));
        DialogResult = true;
    }

}
