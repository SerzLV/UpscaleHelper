using System.Diagnostics;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Tests;

public class ToolRunnerTests
{
    [Fact]
    public async Task CancellationCompletesWithoutWaitingForNaturalExit()
    {
        using var token = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ToolRunner().RunAsync(
            "powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 20"], token.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task PreservesThePipelineFailureWhenStderrHasOutput()
    {
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cause = new IOException("upscaler missed a frame");
        var exception = await Assert.ThrowsAsync<IOException>(() => new ToolRunner().RunAsync(
            "powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Error.WriteLine('encoder context'); Start-Sleep -Seconds 20"],
            token.Token, onErrorLine: _ => ready.TrySetResult(), readOutput: async (_, ct) =>
            {
                await ready.Task.WaitAsync(ct);
                throw cause;
            }));
        Assert.Same(cause, exception);
    }

    [Fact]
    public async Task CancellationIsBoundedEvenWhenAnIoCallbackDoesNotFinish()
    {
        using var token = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new ToolRunner().RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 20"], token.Token,
            readOutput: (_, _) =>
            {
                started.SetResult();
                return release.Task;
            });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            token.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.WaitAsync(TimeSpan.FromSeconds(8)));
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
