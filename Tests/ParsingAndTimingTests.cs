using System.Globalization;
using UpscaleHelper.Localization;
using UpscaleHelper.Presentation;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Tests;

public class ParsingAndTimingTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData(" 1:23 ", 83)]
    [InlineData("1:02:03", 3723)]
    [InlineData("120:00", 7200)]
    public void ReadsTimecodes(string text, int seconds)
    {
        Assert.True(TimecodeInput.TryRead(text, out var result));
        Assert.Equal(TimeSpan.FromSeconds(seconds), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-2")]
    [InlineData("1:60")]
    [InlineData("1::2")]
    [InlineData("1:2:3:4")]
    [InlineData("2147483647:00:00")]
    public void RejectsInvalidTimecodes(string text) => Assert.False(TimecodeInput.TryRead(text, out _));

    [Theory]
    [InlineData("30", 30)]
    [InlineData("30000/1001", 29.97002997003)]
    [InlineData("240", 240)]
    public void ReadsFrameRates(string text, double rate)
    {
        Assert.True(MediaTools.TryFrameRate(text, out double parsed));
        Assert.Equal(rate, parsed, 9);
    }

    [Theory]
    [InlineData("1/2/3")]
    [InlineData("0/0")]
    [InlineData("30/0")]
    [InlineData("241")]
    [InlineData("")]
    public void RejectsInvalidFrameRates(string text) => Assert.False(MediaTools.TryFrameRate(text, out _));

    [Theory]
    [InlineData(null, "en-GB", true)]
    [InlineData(null, "ru-RU", false)]
    [InlineData(null, "lv-LV", true)]
    [InlineData("en", "ru-RU", true)]
    [InlineData("ru", "en-US", false)]
    public void ChoosesLanguage(string? preference, string locale, bool english) =>
        Assert.Equal(english, InterfaceLanguage.UseEnglish(preference, CultureInfo.GetCultureInfo(locale)));

    [Fact]
    public void CalculatesProgressAndEtaWithControlledClock()
    {
        var clock = new TestClock();
        var updates = new List<UpscaleProgress>();
        var timing = new FrameTiming(100, new InlineProgress<UpscaleProgress>(updates.Add), clock);
        timing.Report(0);
        clock.Advance(0.1);
        timing.Report(1);
        Assert.Single(updates);
        clock.Advance(1.9);
        timing.Report(20);
        Assert.Equal(20, updates[^1].Percent);
        Assert.Equal(10, updates[^1].Frames!.FramesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(8), updates[^1].Frames!.Remaining);
        timing.Finish(98);
        Assert.Equal(99, updates[^1].Percent);
        Assert.Equal(98, updates[^1].Frames!.EstimatedTotal);
    }

    [Fact]
    public void FrameWindowKeepsSourceOffsetAndOutputFrameGrid()
    {
        var source = new ComparisonTrack("source", TimeSpan.FromSeconds(5));
        var result = new ComparisonTrack("result", TimeSpan.Zero);
        var clip = new ComparisonClip("proxy", 240, "30", 30, source, result);
        Assert.Equal((TimeSpan.FromSeconds(5), 15), clip.FrameWindow(source, 15));
        Assert.Equal((TimeSpan.FromSeconds(8), 60), clip.FrameWindow(source, 150));
        Assert.Equal((TimeSpan.FromSeconds(3), 60), clip.FrameWindow(result, 150));
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.FrameWindow(source, 240));
    }

    [Fact]
    public void LimitsOversizedVideoBeforeInference()
    {
        OutputLimits.Validate(3840, 2160, 2, 4, MediaKind.Video);
        Assert.Throws<InvalidDataException>(() => OutputLimits.Validate(3840, 2160, 4, 4, MediaKind.Video));
        Assert.Throws<InvalidDataException>(() => OutputLimits.Validate(int.MaxValue, int.MaxValue, 4, 4, MediaKind.Image));
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(double seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
