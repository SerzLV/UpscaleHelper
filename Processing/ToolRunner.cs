using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using UpscaleHelper.Infrastructure;

namespace UpscaleHelper.Processing;

internal sealed class ToolRunner
{
    public async Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token,
        string? workingDirectory = null, Action<string>? onErrorLine = null,
        Func<Stream, CancellationToken, Task>? readOutput = null,
        Func<Stream, CancellationToken, Task>? writeInput = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = readOutput is not null,
            RedirectStandardInput = writeInput is not null,
            WorkingDirectory = workingDirectory ?? string.Empty,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        string tool = Path.GetFileName(executable);
        long started = Stopwatch.GetTimestamp();
        DiagnosticLog.Write($"Starting {tool}.");
        try { process.Start(); }
        catch (Win32Exception ex)
        {
            DiagnosticLog.Write($"{tool} could not start ({ex.NativeErrorCode}).");
            throw new InvalidOperationException(T("ToolStartFailed", tool), ex);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        ExceptionDispatchInfo? pumpFailure = null;
        using var registration = lifetime.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception ex) { DiagnosticLog.Write($"{tool} could not be terminated ({ex.NativeErrorCode})."); }
        });
        var diagnostics = new Queue<string>();
        var stderr = PumpAsync(async () =>
        {
            while (await process.StandardError.ReadLineAsync(lifetime.Token) is { } line)
            {
                lock (diagnostics)
                {
                    if (diagnostics.Count == 8) diagnostics.Dequeue();
                    diagnostics.Enqueue(line);
                }
                onErrorLine?.Invoke(line);
            }
        });
        var output = readOutput is null ? Task.CompletedTask :
            PumpAsync(() => readOutput(process.StandardOutput.BaseStream, lifetime.Token));
        var input = writeInput is null ? Task.CompletedTask : PumpAsync(async () =>
        {
            await using var pipe = process.StandardInput.BaseStream;
            await writeInput(pipe, lifetime.Token);
        });

        var all = Task.WhenAll(process.WaitForExitAsync(lifetime.Token), stderr, output, input);
        try
        {
            await all.WaitAsync(lifetime.Token);
        }
        catch (Exception) when (lifetime.IsCancellationRequested)
        {
            try { await all.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException)
            {
                DiagnosticLog.Write($"{tool} pipe shutdown exceeded 3 seconds.");
                _ = all.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception) { } // Report the initiating error rather than a downstream pipe cancellation.
            DiagnosticLog.Write($"{tool} stopped after {Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s.");
            token.ThrowIfCancellationRequested();
            if (pumpFailure is { SourceException: { } cause })
                DiagnosticLog.Write($"{tool} pipeline error: {cause.GetType().Name} ({cause.HResult:X8}).");
            if (pumpFailure is { SourceException: IOException io } &&
                (io.HResult & 0xffff) is 109 or 232 or 233 && process.HasExited && process.ExitCode != 0)
                throw ToolFailure(io);
            pumpFailure?.Throw();
            throw;
        }
        token.ThrowIfCancellationRequested();
        DiagnosticLog.Write($"{tool} exited with {process.ExitCode} after {Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s.");
        if (process.ExitCode != 0)
            throw ToolFailure();

        async Task PumpAsync(Func<Task> pump)
        {
            try { await pump(); }
            catch (Exception ex)
            {
                if (!lifetime.IsCancellationRequested)
                {
                    Interlocked.CompareExchange(ref pumpFailure, ExceptionDispatchInfo.Capture(ex), null);
                    lifetime.Cancel();
                }
                throw;
            }
        }

        InvalidOperationException ToolFailure(Exception? inner = null)
        {
            lock (diagnostics)
                return new($"{T("ToolFailed", tool, process.ExitCode)}\n{string.Join("\n", diagnostics)}", inner);
        }
    }
}
