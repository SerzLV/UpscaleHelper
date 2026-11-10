using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using UpscaleHelper.Infrastructure;

namespace UpscaleHelper.Processing;

internal sealed class FluxSession(FluxRuntime runtime, string script, ToolRunner runner) : IDisposable
{
    private readonly SemaphoreSlim requests = new(1);
    private readonly object gate = new();
    private Worker? worker;
    private bool disposed;

    public bool IsAvailable => runtime.IsAvailable && File.Exists(script);

    public async Task RunAsync(string input, string raw, string corrected, bool releaseGpu, IProgress<UpscaleProgress> progress,
        CancellationToken token)
    {
        await requests.WaitAsync(token);
        try
        {
            Worker current;
            Worker? previous = null;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (worker?.Completion.IsCompleted != false)
                {
                    previous = worker;
                    worker = new(runtime, script, runner);
                }
                current = worker;
            }
            if (previous is not null) await previous.StopAsync();
            bool cold = !current.HasCompletedRequest;
            long started = Stopwatch.GetTimestamp();
            try
            {
                progress.Report(new(T(cold ? "FluxLoading" : "FluxDrawing"), null));
                await current.RunAsync(new(input, raw, corrected, releaseGpu), progress, token);
                DiagnosticLog.Write(FormattableString.Invariant(
                    $"FLUX request finished in {Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s ({(cold ? "cold" : "warm")})."));
            }
            catch
            {
                if (Detach(current)) await current.StopAsync();
                throw;
            }
        }
        finally { requests.Release(); }
    }

    public void Dispose()
    {
        Worker? current;
        lock (gate)
        {
            disposed = true;
            current = worker;
            worker = null;
        }
        current?.StopAsync().GetAwaiter().GetResult();
    }

    private bool Detach(Worker current)
    {
        lock (gate)
        {
            if (worker != current) return false;
            worker = null;
            return true;
        }
    }

    private sealed record Request(string Input, string Raw, string Corrected, bool ReleaseGpu);

    private sealed class Worker
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly Channel<string> requests = Channel.CreateUnbounded<string>();
        private readonly Channel<string> responses = Channel.CreateUnbounded<string>();
        public Task Completion { get; }
        public bool HasCompletedRequest { get; private set; }

        public Worker(FluxRuntime runtime, string script, ToolRunner runner)
        {
            string[] dependencies = runtime.DependencyDirectory is { } path ? ["--dependencies", path] : [];
            string[] arguments = ["-I", "-B", "-X", "utf8", script, "--comfy", runtime.ComfyDirectory,
                "--models", runtime.ModelDirectory, .. dependencies];
            Completion = Task.Run(async () =>
            {
                DiagnosticLog.Write("Starting FLUX session.");
                try
                {
                    await runner.RunAsync(runtime.PythonPath, arguments, lifetime.Token,
                        readOutput: ReadResponsesAsync, writeInput: WriteRequestsAsync);
                }
                finally
                {
                    requests.Writer.TryComplete();
                    responses.Writer.TryComplete();
                    DiagnosticLog.Write("FLUX session ended.");
                }
            });
        }

        public async Task RunAsync(Request request, IProgress<UpscaleProgress> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (requests.Writer.TryWrite(JsonSerializer.Serialize(request)))
            {
                await foreach (string line in responses.Reader.ReadAllAsync(token))
                {
                    token.ThrowIfCancellationRequested();
                    if (line == "flux:drawing") progress.Report(new(T("FluxDrawing"), null));
                    if (line != "flux:done") continue;
                    HasCompletedRequest = true;
                    return;
                }
            }
            await Completion;
            throw new InvalidDataException(T("FluxSessionEnded"));
        }

        public async Task StopAsync()
        {
            lifetime.Cancel();
            try { await Completion.ConfigureAwait(false); }
            catch (Exception) { } // The active request observes the worker error.
            finally { lifetime.Dispose(); }
        }

        private async Task ReadResponsesAsync(Stream stream, CancellationToken token)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    if (line.StartsWith("flux:", StringComparison.Ordinal)) responses.Writer.TryWrite(line);
                }
            }
            finally
            {
                responses.Writer.TryComplete();
                requests.Writer.TryComplete();
            }
        }

        private async Task WriteRequestsAsync(Stream stream, CancellationToken token)
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            await foreach (string request in requests.Reader.ReadAllAsync(token))
            {
                await writer.WriteLineAsync(request.AsMemory(), token);
                await writer.FlushAsync(token);
            }
        }
    }
}
