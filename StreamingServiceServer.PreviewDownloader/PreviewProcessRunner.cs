using System.Diagnostics;
using System.Text;

namespace StreamingServiceServer.PreviewDownloader;

public interface IPreviewProcessRunner
{
    Task<string> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken);
}

public sealed class PreviewProcessRunner : IPreviewProcessRunner
{
    private const int DiagnosticCharacterLimit = 8192;
    private const int DrainBufferLength = 4096;
    private static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(5);

    public async Task<string> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var drainCancellation = new CancellationTokenSource();
        var standardOutput = DrainAsync(process.StandardOutput, drainCancellation.Token);
        var standardError = DrainAsync(process.StandardError, drainCancellation.Token);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var output = await standardOutput.WaitAsync(ShutdownDeadline, cancellationToken);
            var error = await standardError.WaitAsync(ShutdownDeadline, cancellationToken);
            if (process.ExitCode != 0)
                throw new IOException($"Preview process failed with exit code {process.ExitCode}: {error}");
            return output;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            try { await process.WaitForExitAsync().WaitAsync(ShutdownDeadline); }
            catch (TimeoutException) { }
            drainCancellation.Cancel();
            try { await Task.WhenAll(standardOutput, standardError).WaitAsync(ShutdownDeadline); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[DrainBufferLength];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) return output.ToString();
            var retainedCount = Math.Min(count, DiagnosticCharacterLimit - output.Length);
            if (retainedCount > 0) output.Append(buffer, 0, retainedCount);
        }
    }
}
