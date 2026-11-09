using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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
        try { process.Start(); }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(T("ToolStartFailed", Path.GetFileName(executable)), ex);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var registration = lifetime.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        });
        var diagnostics = new Queue<string>();
        var stderr = PumpAsync(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (diagnostics.Count == 8) diagnostics.Dequeue();
                diagnostics.Enqueue(line);
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

        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), stderr, output, input);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException && diagnostics.Count > 0)
        {
            throw ToolFailure(ex);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            throw;
        }
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw ToolFailure();

        async Task PumpAsync(Func<Task> pump)
        {
            try { await pump(); }
            catch
            {
                lifetime.Cancel();
                throw;
            }
        }

        InvalidOperationException ToolFailure(Exception? inner = null) => new(
            $"{T("ToolFailed", Path.GetFileName(executable), process.ExitCode)}\n{string.Join("\n", diagnostics)}", inner);
    }
}
