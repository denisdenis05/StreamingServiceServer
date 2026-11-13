using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images.Assets;

namespace StreamingServiceServer.Business.Services.Images.Playlists;

public interface IPlaylistCollageRenderer
{
    Task<bool> RenderAsync(IReadOnlyList<string> sources, string output, CancellationToken cancellationToken);
}

public sealed class PlaylistCollageRenderer(IOptions<CoverAssetOptions> options) : IPlaylistCollageRenderer
{
    public const int PolicyVersion = 1;
    public const int TileCount = 4;
    public const int MaximumDimension = 500;
    public const string GeneratedPrefix = "playlist-";
    public const string ImageExtension = ".jpg";
    private const int HeaderLimitBytes = 262144;
    private const int MinimumRetryDimension = 32;
    private const int GridSide = 2;
    private const int ProcessThreads = 1;
    private const int DrainCharacters = 4096;
    private static readonly int[] QualityLevels = [4, 10, 20, 31];
    private static readonly SemaphoreSlim Admission = new(ProcessThreads, ProcessThreads);

    public async Task<bool> RenderAsync(IReadOnlyList<string> sources, string output, CancellationToken cancellationToken)
    {
        if (sources.Count == 0 || sources.Count > TileCount) return false;
        var configuration = options.Value;
        var dimensions = new List<int>();
        foreach (var source in sources)
        {
            if (new FileInfo(source).Length > configuration.MaximumInputBytes) throw new IOException("Playlist cover exceeds the input limit.");
            await CoverImageAdmission.ValidateAsync(source, configuration.MaximumDecodedPixels, cancellationToken);
            await using var input = File.OpenRead(source);
            var header = new byte[Math.Min(input.Length, HeaderLimitBytes)];
            await input.ReadExactlyAsync(header, cancellationToken);
            var size = CoverImageAdmission.ReadDimensions(header);
            dimensions.Add(Math.Min(size.Width, size.Height));
        }
        var dimension = Math.Min(MaximumDimension, dimensions.Min()) / GridSide * GridSide;
        var maximumBytes = Math.Min(configuration.MaximumOutputBytes, sources.Min(source => new FileInfo(source).Length));
        await Admission.WaitAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(configuration.RenderTimeout);
        try
        {
            while (dimension >= GridSide)
            {
                foreach (var quality in QualityLevels)
                {
                    await RenderAttemptAsync(sources, output, dimension, quality, deadline.Token);
                    if (new FileInfo(output).Length <= maximumBytes) return true;
                }
                if (dimension <= MinimumRetryDimension) break;
                dimension /= GridSide;
                dimension = dimension / GridSide * GridSide;
            }
            File.Delete(output);
            return false;
        }
        finally { Admission.Release(); }
    }

    private async Task RenderAttemptAsync(IReadOnlyList<string> sources, string output, int dimension, int quality, CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(configuration.RenderTimeout);
        var information = new ProcessStartInfo(configuration.RendererExecutable)
            { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        Add(information, "-hide_banner", "-loglevel", "error", "-nostdin", "-y");
        for (var tile = 0; tile < TileCount; tile++) Add(information, "-threads", ProcessThreads.ToString(CultureInfo.InvariantCulture),
            "-protocol_whitelist", "file,pipe", "-i", sources[tile % sources.Count]);
        Add(information, "-filter_complex_threads", ProcessThreads.ToString(CultureInfo.InvariantCulture), "-filter_complex", Filters(dimension),
            "-map", "[collage]", "-map_metadata", "-1", "-frames:v", ProcessThreads.ToString(CultureInfo.InvariantCulture),
            "-threads", ProcessThreads.ToString(CultureInfo.InvariantCulture), "-c:v", "mjpeg", "-q:v", quality.ToString(CultureInfo.InvariantCulture), "-f", "image2", output);
        using var process = Process.Start(information) ?? throw new IOException("Playlist artwork could not be rendered.");
        var errors = DrainAsync(process.StandardError, timeout.Token);
        var standardOutput = DrainAsync(process.StandardOutput, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(errors, standardOutput);
            if (process.ExitCode != 0 || !File.Exists(output)) throw new IOException("Playlist artwork could not be rendered.");
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(errors, standardOutput); } catch (OperationCanceledException) { }
            throw;
        }
    }

    private static string Filters(int dimension)
    {
        var tile = (dimension / GridSide).ToString(CultureInfo.InvariantCulture);
        var branches = Enumerable.Range(0, TileCount).Select(index => $"[{index}:v]scale={tile}:{tile}:force_original_aspect_ratio=increase,crop={tile}:{tile},setsar=1[tile{index}]");
        return string.Join(';', branches) + $";[tile0][tile1][tile2][tile3]xstack=inputs=4:layout=0_0|{tile}_0|0_{tile}|{tile}_{tile}[collage]";
    }
    private static void Add(ProcessStartInfo information, params string[] arguments)
    {
        foreach (var argument in arguments) information.ArgumentList.Add(argument);
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[DrainCharacters];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) != 0) { }
    }
}
