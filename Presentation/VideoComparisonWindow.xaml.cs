using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

public partial class VideoComparisonWindow : Window
{
    private readonly ComparisonClip clip;
    private readonly IVideoFrameProvider frames;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly ComparisonViewModel frameComparison = new();
    private int? loadedFrame;
    private CancellationTokenSource? frameLoad;
    private Task frameTask = Task.CompletedTask;
    private int frameIndex;
    private bool mediaReady;
    private bool playing;
    private bool updatingTimeline;
    private bool closing;

    internal VideoComparisonWindow(ComparisonClip clip, string name, IVideoFrameProvider frames)
    {
        this.clip = clip;
        this.frames = frames;
        InitializeComponent();
        FrameComparison.DataContext = frameComparison;
        FileLabel.Text = name;
        FileLabel.ToolTip = name;
        if (clip.EstimatedExportBytes is { } bytes)
        {
            ExportEstimate.Text = T("ExportEstimate", FileSize.Format(bytes));
            ExportEstimate.Visibility = Visibility.Visible;
        }
        timer.Tick += (_, _) =>
        {
            if (playing) SetFrameIndex(PlaybackFrame());
        };
    }

    private int LastFrame => clip.FrameCount - 1;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Timeline.Maximum = LastFrame;
        Timeline.IsEnabled = LastFrame > 0;
        SetFrameIndex(0);
        Player.Source = new Uri(clip.Path, UriKind.Absolute);
        Player.Play();
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        mediaReady = true;
        PlaybackMessage.Visibility = Visibility.Collapsed;
        PlayButton.IsEnabled = true;
        SoundButton.IsEnabled = Player.HasAudio;
        if (FrameLayer.Visibility == Visibility.Visible)
            Player.Pause();
        else
        {
            playing = true;
            PlayButton.Content = T("Pause");
            timer.Start();
        }
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (playing && !closing) ShowFrame(LastFrame);
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (closing) return;
        mediaReady = false;
        PlayButton.IsEnabled = false;
        SoundButton.IsEnabled = false;
        ToolTipService.SetShowOnDisabled(PlayButton, true);
        PlayButton.ToolTip = T("PlaybackFailed", e.ErrorException.Message);
        ShowFrame(frameIndex);
    }

    private void OnTogglePlay(object sender, RoutedEventArgs e)
    {
        if (!mediaReady || closing) return;
        if (playing)
        {
            ShowFrame(PlaybackFrame());
            return;
        }

        frameLoad?.Cancel();
        FrameLayer.Visibility = Visibility.Collapsed;
        PlaybackLabels.Visibility = Visibility.Visible;
        int start = frameIndex == LastFrame ? 0 : frameIndex;
        SetFrameIndex(start);
        Player.Position = clip.PositionOf(start);
        Player.Play();
        playing = true;
        PlayButton.Content = T("Pause");
        timer.Start();
    }

    private void OnToggleSound(object sender, RoutedEventArgs e)
    {
        Player.IsMuted = !Player.IsMuted;
        SoundButton.Content = Player.IsMuted ? T("Unmute") : T("Mute");
    }

    private void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Timeline?.IsEnabled == true && !updatingTimeline && !closing)
            ShowFrame((int)Math.Round(e.NewValue));
    }

    private void OnPreviousFrame(object sender, RoutedEventArgs e) => Step(-1);
    private void OnNextFrame(object sender, RoutedEventArgs e) => Step(1);

    private void OnFrameKey(object sender, KeyEventArgs e)
    {
        if (FrameComparison.IsKeyboardFocusWithin || Keyboard.Modifiers != ModifierKeys.None ||
            e.Key is not (Key.Left or Key.Right)) return;
        Step(e.Key == Key.Left ? -1 : 1);
        e.Handled = true;
    }

    private void Step(int delta)
    {
        if (closing) return;
        int current = playing ? PlaybackFrame() : frameIndex;
        ShowFrame(Math.Clamp(current + delta, 0, LastFrame));
    }

    private int PlaybackFrame() =>
        Math.Clamp((int)(Player.Position.TotalSeconds * clip.FramesPerSecond + 0.0001), 0, LastFrame);

    private void SetFrameIndex(int index)
    {
        frameIndex = index;
        updatingTimeline = true;
        Timeline.Value = index;
        updatingTimeline = false;
        var position = TimeSpan.FromMilliseconds(Math.Round(clip.PositionOf(index).TotalMilliseconds));
        string time = position.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
        PositionLabel.Text = position.TotalHours >= 1 ? $"{(long)position.TotalHours}:{time}" : time;
        FrameLabel.Text = T("FrameNumber", index + 1, clip.FrameCount);
        PreviousFrame.IsEnabled = index > 0;
        NextFrame.IsEnabled = index < LastFrame;
    }

    private void ShowFrame(int index)
    {
        timer.Stop();
        if (mediaReady) Player.Pause();
        playing = false;
        PlayButton.Content = index == LastFrame ? T("Replay") : T("Resume");
        SetFrameIndex(index);
        FrameLayer.Visibility = Visibility.Visible;
        PlaybackLabels.Visibility = Visibility.Collapsed;
        frameLoad?.Cancel();
        if (loadedFrame == index)
        {
            FrameStatus.Visibility = Visibility.Collapsed;
            return;
        }

        FrameMessage.Text = T("LoadingFrame");
        FrameStatus.Visibility = Visibility.Visible;
        frameTask = LoadFrameAsync(index, frameTask);
    }

    private async Task LoadFrameAsync(int index, Task previous)
    {
        using var request = new CancellationTokenSource();
        frameLoad = request;
        try
        {
            await previous;
            await Task.Delay(45, request.Token);
            var original = LoadSideAsync(clip.Original);
            var enhanced = LoadSideAsync(clip.Enhanced);
            await Task.WhenAll(original, enhanced);
            request.Token.ThrowIfCancellationRequested();
            frameComparison.ShowFrames(await original, await enhanced);
            loadedFrame = index;
            FrameStatus.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested) FrameMessage.Text = T("ReadFrameFailed", ex.Message);
        }
        finally
        {
            if (frameLoad == request) frameLoad = null;
        }

        Task<PreviewImage> LoadSideAsync(ComparisonTrack track) => Task.Run(async () =>
            PreviewImage.Decode(await frames.ReadFrameAsync(clip, track, index, request.Token)), request.Token);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (frameTask.IsCompleted) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        timer.Stop();
        frameLoad?.Cancel();
        await frameTask;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        timer.Stop();
        Player.Close();
    }
}
