using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Background;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Data;
using Xunit;

namespace StreamingServiceServer.CoverTests;

[Trait("Category", "Postgres")]
public sealed class AlbumCoverFallbackJobTests
{
    [PostgresFact]
    public async Task ArchiveTimeoutUsesMatchedAlbumAndPublishesAllVariantsWithoutTryingOtherArchiveSizes()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var release = await database.Releases.SingleAsync();
            release.Cover = "https://ia601205.us.archive.org/example.jpg";
            release.SmallCover = "https://ia601205.us.archive.org/example-small.jpg";
            release.VerySmallCover = "https://ia601205.us.archive.org/example-thumbnail.jpg";
            await database.SaveChangesAsync();
        }
        using var handler = new Images();
        using var store = new CoverAssetStore(() => new HttpClient(handler, false), fixture.Assets.Renderer,
            Options.Create(fixture.Assets.Options), TimeProvider.System);
        var fallback = new Lookup();
        var worker = new CoverAssetJobService(fixture.Services.GetRequiredService<IServiceScopeFactory>(), store,
            Options.Create(fixture.Assets.Options), fixture.Clock, NullLogger<CoverAssetJobService>.Instance, fallback);
        await worker.ProcessDueAsync(CancellationToken.None);
        using var verification = fixture.Services.CreateScope();
        var current = verification.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var album = await current.Releases.SingleAsync();
        Assert.NotEmpty(album.LocalCover);
        Assert.NotEmpty(album.LocalSmallCover);
        Assert.NotEmpty(album.LocalVerySmallCover);
        Assert.Equal("https://ia601205.us.archive.org/example.jpg", album.Cover);
        Assert.NotNull((await current.CoverAssetJobs.SingleAsync()).CompletedAt);
        Assert.Equal(("test artist", "test release"), Assert.Single(fallback.Requests));
        Assert.Equal(2, handler.Requests.Count);
    }

    [PostgresFact]
    public async Task AnUnrelatedSourceDoesNotTriggerAlbumReplacement()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.AddReleaseAsync();
        using var handler = new Images();
        using var store = new CoverAssetStore(() => new HttpClient(handler, false), fixture.Assets.Renderer,
            Options.Create(fixture.Assets.Options), TimeProvider.System);
        var fallback = new Lookup();
        var worker = new CoverAssetJobService(fixture.Services.GetRequiredService<IServiceScopeFactory>(), store,
            Options.Create(fixture.Assets.Options), fixture.Clock, NullLogger<CoverAssetJobService>.Instance, fallback);
        await worker.ProcessDueAsync(CancellationToken.None);
        Assert.Empty(fallback.Requests);
        using var scope = fixture.Services.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.SingleAsync();
        Assert.Null(job.CompletedAt);
        Assert.Equal(1, job.Attempts);
    }

    private sealed class Lookup : IAlbumCoverFallback
    {
        public List<(string Artist, string Album)> Requests { get; } = new();
        public Task<Uri?> ResolveAsync(string artist, string album, CancellationToken cancellationToken)
        {
            Requests.Add((artist, album));
            return Task.FromResult<Uri?>(new Uri("https://cdn-images.dzcdn.net/cover.jpg"));
        }
    }

    private sealed class Images : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host != "cdn-images.dzcdn.net") throw new TaskCanceledException("Upstream request timed out.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(AssetFixture.Image) });
        }
    }
}
