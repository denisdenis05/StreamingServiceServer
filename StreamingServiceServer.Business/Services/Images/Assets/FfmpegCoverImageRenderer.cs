using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace StreamingServiceServer.Business.Services.Images.Assets;

public sealed class FfmpegCoverImageRenderer(IOptions<CoverAssetOptions> configuration) : ICoverImageRenderer
{
    private const string FailureMessage = "Cover image decoding failed.";
    private const int DrainBufferCharacters = 4096;
    private const int MaximumDiagnosticCharacters = 8192;
    private const int MaximumSingleAllocationBytes = 67108864;

    public Task RenderAsync(string input, string output, int maximumDimension, CancellationToken cancellationToken) =>
        RenderVariantsAsync(input, new[] { new CoverRenderOutput(output, maximumDimension) }, cancellationToken);

    public async Task RenderVariantsAsync(string input, IReadOnlyList<CoverRenderOutput> outputs, CancellationToken cancellationToken)
    {
        if (outputs.Count == 0 || outputs.Any(output => output.MaximumDimension <= 0))
            throw new ArgumentException("At least one positive output dimension is required.", nameof(outputs));
        var options = configuration.Value;
        await CoverImageAdmission.ValidateAsync(input, options.MaximumDecodedPixels, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RenderTimeout);
        var start = new ProcessStartInfo(options.RendererExecutable)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
        };
        AddArguments(start, "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-max_alloc",
            MaximumSingleAllocationBytes.ToString(CultureInfo.InvariantCulture), "-threads", "1", "-i", input,
            "-filter_complex_threads", "1", "-filter_complex", FilterGraph(outputs));
        for (var outputIndex = 0; outputIndex < outputs.Count; outputIndex++)
            AddArguments(start, "-map", $"[output{outputIndex}]", "-map_metadata", "-1", "-frames:v", "1",
                "-threads", "1", "-q:v", "3", "-f", "image2", "-c:v", "mjpeg", outputs[outputIndex].Path);
        using var process = Process.Start(start) ?? throw new IOException(FailureMessage);
        var errors = DrainAsync(process.StandardError, timeout.Token);
        var standardOutput = DrainAsync(process.StandardOutput, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(errors, standardOutput);
            if (process.ExitCode != 0 || outputs.Any(output => !File.Exists(output.Path) || new FileInfo(output.Path).Length == 0))
                throw new IOException(FailureMessage);
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(errors, standardOutput); } catch (OperationCanceledException) { }
            throw;
        }
    }

    internal static string FilterGraph(IReadOnlyList<CoverRenderOutput> outputs)
    {
        var branches = string.Concat(Enumerable.Range(0, outputs.Count).Select(index => $"[input{index}]"));
        var filters = outputs.Select((output, index) =>
        {
            var dimension = output.MaximumDimension.ToString(CultureInfo.InvariantCulture);
            return $"[input{index}]scale=w='min({dimension},iw)':h='min({dimension},ih)':force_original_aspect_ratio=decrease[output{index}]";
        });
        return $"[0:v]split={outputs.Count}{branches};" + string.Join(';', filters);
    }

    private static void AddArguments(ProcessStartInfo information, params string[] arguments)
    {
        foreach (var argument in arguments) information.ArgumentList.Add(argument);
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[DrainBufferCharacters];
        var retained = new System.Text.StringBuilder(MaximumDiagnosticCharacters);
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) is var characters && characters != 0)
        {
            var remaining = MaximumDiagnosticCharacters - retained.Length;
            if (remaining > 0) retained.Append(buffer, 0, Math.Min(remaining, characters));
        }
        return retained.ToString();
    }
}
