using System.Diagnostics;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images.Assets;
using Xunit;
namespace StreamingServiceServer.CoverTests;
public sealed class CoverRendererIntegrationTests
{
    [Fact]
    public async Task FfmpegCreatesBoundedFullSmallAndThumbnailImagesWithoutUpscaling()
    {
        var directory = Path.Combine(Path.GetTempPath(), "streaming-cover-render-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.png");
            await RunAsync("ffmpeg", new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=800x600", "-frames:v", "1", "-threads", "1", source });
            var options = new CoverAssetOptions { Path = directory, Domain = "https://app.invalid" };
            var renderer = new FfmpegCoverImageRenderer(Options.Create(options));
            var store = new CoverAssetStore(() => new HttpClient(), renderer, Options.Create(options), TimeProvider.System);
            await using var input = File.OpenRead(source);
            var result = await store.ImportAsync(input, CancellationToken.None);
            foreach (var (variant, maximum) in new[] { (result.Full, options.FullDimension), (result.Small, options.SmallDimension), (result.Thumbnail, options.ThumbnailDimension) })
            {
                var dimensions = (await RunAsync("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", Path.Combine(directory, variant.Filename) })).Trim().Split(',');
                Assert.InRange(int.Parse(dimensions[0]), 1, Math.Min(maximum, 800)); Assert.InRange(int.Parse(dimensions[1]), 1, Math.Min(maximum, 600));
            }
            Assert.True(await store.IsValidAsync(result, CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task CorruptImageSignaturesCannotBypassActualDecoding()
    {
        using var fixture = new AssetFixture();
        var store = new CoverAssetStore(() => new HttpClient(), new FfmpegCoverImageRenderer(Options.Create(fixture.Options)), Options.Create(fixture.Options), TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.asset.json"));
    }
    [Fact]
    public async Task RetentionRemovesOnlyExpiredOwnedAssetsAndPreservesReferencedAndLegacyFiles()
    {
        using var fixture = new AssetFixture(); var assets = await fixture.Store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None);
        var legacy = Path.Combine(fixture.Directory, "user-cover.jpg"); await File.WriteAllTextAsync(legacy, "legacy");
        foreach (var path in Directory.GetFiles(fixture.Directory)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(400));
        await fixture.Store.PruneAsync(new HashSet<string> { assets.Identity, assets.SourceIdentity }, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(fixture.Directory, assets.Full.Filename))); Assert.True(File.Exists(legacy));
        await fixture.Store.PruneAsync(new HashSet<string>(), CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(fixture.Directory, assets.Full.Filename))); Assert.True(File.Exists(legacy));
    }
    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments)
    {
        var information = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) information.ArgumentList.Add(argument);
        using var process = Process.Start(information)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await error); return await output;
    }
}
