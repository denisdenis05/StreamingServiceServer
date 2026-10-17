using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
namespace StreamingServiceServer.Business.Services.Images.Assets;

public sealed class FfmpegCoverImageRenderer(IOptions<CoverAssetOptions> configuration) : ICoverImageRenderer
{
    private const string FailureMessage = "Cover image decoding failed.";
    public async Task RenderAsync(string input, string output, int maximumDimension, CancellationToken cancellationToken)
    {
        var options = configuration.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RenderTimeout);
        var start = new ProcessStartInfo(options.RendererExecutable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        var dimension = maximumDimension.ToString(CultureInfo.InvariantCulture);
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-max_alloc", "67108864",
            "-threads", "1", "-i", input, "-map_metadata", "-1", "-frames:v", "1", "-vf",
            $"scale=w='min({dimension},iw)':h='min({dimension},ih)':force_original_aspect_ratio=decrease", "-threads", "1", "-q:v", "3", "-f", "image2", "-c:v", "mjpeg", output }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException(FailureMessage);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(errors, standardOutput);
            if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException(FailureMessage);
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }
}
