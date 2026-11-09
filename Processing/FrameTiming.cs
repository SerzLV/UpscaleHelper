namespace UpscaleHelper.Processing;

internal sealed class FrameTiming(int? estimatedTotal, IProgress<UpscaleProgress> progress, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly long started = (clock ?? TimeProvider.System).GetTimestamp();
    private readonly Queue<(int Count, double Seconds)> samples = new([(0, 0.0)]);
    private double lastReport = -1;

    public void Report(int count, bool force = false)
    {
        double now = clock.GetElapsedTime(started).TotalSeconds;
        if (!force && now - lastReport < 0.3) return;
        lastReport = now;
        samples.Enqueue((count, now));
        while (samples.Count > 2 && samples.Peek().Seconds < now - 20)
            samples.Dequeue();
        var first = samples.Peek();
        double elapsed = now - first.Seconds;
        int recentFrames = count - first.Count;
        double? fps = recentFrames >= 2 && elapsed >= 1.5 ? recentFrames / elapsed : null;
        double? percent = estimatedTotal is { } total ? Math.Clamp(100.0 * count / total, 0, 98) : null;
        TimeSpan? remaining = estimatedTotal is { } target && target > count && fps is > 0
            ? TimeSpan.FromSeconds((target - count) / fps.Value) : null;
        progress.Report(new(T("UpscalingFrames"), percent, new(count, estimatedTotal, fps, remaining)));
    }

    public void Finish(int count) =>
        progress.Report(new(T("SavingMp4"), 99, new(count, count, null, null)));
}
