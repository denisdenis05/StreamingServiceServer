using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Background;
using StreamingServiceServer.Data.Models;
using Xunit;
namespace StreamingServiceServer.CoverTests;

public sealed class CoverAssetTests
{
    private static readonly Guid ReleaseIdentifier = Guid.Parse("f4903214-5f9a-4963-899b-a6d87595fd83");
    [Theory]
    [InlineData("https://coverartarchive.org/")]
    [InlineData("https://coverartarchive.org/release/")]
    [InlineData("https://coverartarchive.org/release-group/")]
    public void ArchiveRootSupportsLegacyConfigurationWithoutDuplicatingPaths(string configured)
    {
        var service = new ImageService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Music:External:AlbumCover:BaseUrl"] = configured }).Build());
        Assert.Equal($"https://coverartarchive.org/release/{ReleaseIdentifier}/front", service.GetAlbumCover(ReleaseIdentifier).Cover);
        Assert.Equal($"https://coverartarchive.org/release-group/{ReleaseIdentifier}/front-250", service.GetAllAlbumCovers(ReleaseIdentifier).VerySmallCover);
    }
    [Theory]
    [InlineData("https://coverartarchive.org/release/release/example/front", "https://coverartarchive.org/release/example/front")]
    [InlineData("https://coverartarchive.org/release/release-group/example/front", "https://coverartarchive.org/release-group/example/front")]
    [InlineData("https://other.invalid/release/release/example/front", "https://other.invalid/release/release/example/front")]
    public void RepairOnlyChangesKnownArchivePrefixes(string source, string expected) => Assert.Equal(expected, CoverPolicy.RepairArchiveAddress(source));
    [Fact]
    public void EveryResponseUsesLocalThenOptimizedThenOriginal() {
        Assert.Equal("local", CoverPolicy.Select("local", "optimized", "original"));
        Assert.Equal("optimized", CoverPolicy.Select(null, "optimized", "original"));
        Assert.Equal("original", CoverPolicy.Select(null, null, "original"));
    }
    [Fact]
    public void OnlyVersionedFilenamesReceiveImmutableServingHeaders() {
        Assert.Equal(CoverServingPolicy.VersionedCacheControl, CoverServingPolicy.CacheControl(new string('a', 64) + "-v1-small.jpg"));
        Assert.Equal(CoverServingPolicy.LegacyCacheControl, CoverServingPolicy.CacheControl("album_cover.jpg"));
    }
    [Fact]
    public void ChangedSourceResetsPersistedRetryAndLeaseState() {
        var job = new CoverAssetJob { Attempts = 5, LeaseToken = Guid.NewGuid(), CompletedAt = DateTimeOffset.UtcNow, LastFailure = "old" };
        var now = DateTimeOffset.UnixEpoch; CoverJobInterceptor.Schedule(job, "https://images.invalid/new.jpg", now);
        Assert.Equal(0, job.Attempts); Assert.Null(job.LeaseToken); Assert.Null(job.CompletedAt); Assert.Equal(now, job.NextAttemptAt);
        Assert.Equal(string.Empty, job.LastFailure); Assert.NotEqual(Guid.Empty, job.Version);
    }
    [Fact]
    public void PersistedRetriesUseBoundedExponentialBackoff() {
        var options = new CoverAssetOptions();
        Assert.Equal(options.InitialRetryDelay, CoverAssetJobService.RetryDelay(1, options));
        Assert.Equal(options.InitialRetryDelay * 2, CoverAssetJobService.RetryDelay(2, options));
        Assert.Equal(options.MaximumRetryDelay, CoverAssetJobService.RetryDelay(100, options));
    }
    [Fact]
    public async Task CachedSourceAvoidsAnotherHttpDownload() {
        using var fixture = new AssetFixture(); var first = await fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        var second = await fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        Assert.Equal(first, second); Assert.Equal(1, fixture.Handler.Requests);
    }
    [Fact]
    public async Task ConcurrentSourceRequestsShareOneDownload() {
        using var fixture = new AssetFixture();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None)));
        Assert.All(results, result => Assert.Equal(results[0], result)); Assert.Equal(1, fixture.Handler.Requests);
    }
    [Fact]
    public async Task DifferentUrlsWithTheSameBasenameProduceDifferentAssets() {
        using var fixture = new AssetFixture(); var first = await fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        fixture.Handler.Content = AssetFixture.Image.Concat(new byte[] { 1 }).ToArray();
        var second = await fixture.Store.GetOrCreateAsync("https://other.invalid/cover.jpg", CancellationToken.None);
        Assert.NotEqual(first.Identity, second.Identity); Assert.NotEqual(first.Full.Filename, second.Full.Filename);
    }
    [Fact]
    public async Task MissingVariantIsRepairedFromTheRetainedOriginalWithoutHttp() {
        using var fixture = new AssetFixture(); var first = await fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        File.Delete(Path.Combine(fixture.Directory, first.Small.Filename));
        Assert.False(await fixture.Store.IsValidAsync(first, CancellationToken.None));
        var repaired = await fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        Assert.True(await fixture.Store.IsValidAsync(repaired, CancellationToken.None)); Assert.Equal(1, fixture.Handler.Requests);
    }
    [Fact]
    public async Task SameLengthCorruptionIsNotAcceptedAsAValidAsset() {
        using var fixture = new AssetFixture(); var assets = await fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Directory, assets.Thumbnail.Filename), new byte[assets.Thumbnail.Bytes]);
        Assert.False(await fixture.Store.IsValidAsync(assets, CancellationToken.None));
    }
    [Fact]
    public async Task InvalidDownloadedContentCannotCreateAnAssetManifest() {
        using var fixture = new AssetFixture(); fixture.Handler.Content = Encoding.UTF8.GetBytes("not an image");
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.asset.json"));
    }
    [Fact]
    public async Task OversizedDownloadsAreRejectedBeforePublication() {
        using var fixture = new AssetFixture(); fixture.Options.MaximumInputBytes = 1;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.asset.json"));
    }
    [Fact]
    public async Task RendererFailureLeavesNoCompletedOrTemporaryAsset() {
        using var fixture = new AssetFixture(); fixture.Renderer.Fail = true;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.asset.json"));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.partial"));
    }
    [Fact]
    public async Task EveryTierHasItsOwnOutputAndChangedUploadHasANewIdentity() {
        using var fixture = new AssetFixture();
        var first = await fixture.Store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None);
        var second = await fixture.Store.ImportAsync(new MemoryStream(AssetFixture.Image.Concat(new byte[] { 1 }).ToArray()), CancellationToken.None);
        Assert.Equal(new[] { fixture.Options.FullDimension, fixture.Options.SmallDimension, fixture.Options.ThumbnailDimension }, fixture.Renderer.Dimensions.Take(3));
        Assert.NotEqual(first.Full.Filename, first.Small.Filename); Assert.NotEqual(first.Small.Filename, first.Thumbnail.Filename);
        Assert.NotEqual(fixture.Store.PublicAddress(first.Full), fixture.Store.PublicAddress(second.Full));
    }
    [Fact]
    public async Task ResizingConfigurationChangesCreateNewImmutableNamesWithoutAnotherDownload() {
        using var fixture = new AssetFixture(); var first = await fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        fixture.Options.SmallDimension = 300;
        var second = await fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        Assert.NotEqual(first.Small.Filename, second.Small.Filename); Assert.Equal(1, fixture.Handler.Requests);
        Assert.True(File.Exists(Path.Combine(fixture.Directory, first.Small.Filename)));
    }
    [Fact]
    public async Task OwnVersionedPublicUrlsReuseOriginalAssetsWithoutDownloadingOrEncodingAgain() {
        using var fixture = new AssetFixture(); var first = await fixture.Store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None);
        var second = await fixture.Store.GetOrCreateAsync(fixture.Store.PublicAddress(first.Full), CancellationToken.None);
        Assert.Equal(first, second); Assert.Equal(0, fixture.Handler.Requests); Assert.Equal(3, fixture.Renderer.Dimensions.Count);
    }
}
internal sealed class AssetFixture : IDisposable
{
    public const string Source = "https://images.invalid/cover.jpg";
    public static readonly byte[] Image = new byte[] { 0xff, 0xd8, 0xff, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "streaming-cover-unit-" + Guid.NewGuid().ToString("N"));
    public CoverAssetOptions Options { get; }
    public ImageHandler Handler { get; } = new();
    public ImageRenderer Renderer { get; } = new();
    public CoverAssetStore Store { get; }
    public AssetFixture() {
        Options = new CoverAssetOptions { Path = Directory, Domain = "https://app.invalid" };
        Store = new CoverAssetStore(() => new HttpClient(Handler, false), Renderer, Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
    }
    public void Dispose() { Store.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); Handler.Dispose(); }
}
internal sealed class ImageHandler : HttpMessageHandler
{
    public int Requests; public Func<Task>? Wait { get; set; } public byte[] Content { get; set; } = AssetFixture.Image;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        Interlocked.Increment(ref Requests);
        if (Wait is not null) await Wait();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) };
    }
}
internal sealed class ImageRenderer : ICoverImageRenderer
{
    public List<int> Dimensions { get; } = new(); public bool Fail { get; set; }
    public Task RenderAsync(string input, string output, int maximumDimension, CancellationToken cancellationToken) {
        if (Fail) throw new IOException("fixture renderer failure");
        Dimensions.Add(maximumDimension); return File.WriteAllBytesAsync(output, AssetFixture.Image.Concat(BitConverter.GetBytes(maximumDimension)).ToArray(), cancellationToken);
    }
}
