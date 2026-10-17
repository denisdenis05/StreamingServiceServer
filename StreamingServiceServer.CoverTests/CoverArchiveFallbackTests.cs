using System.Net;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images.Assets;
using Xunit;
namespace StreamingServiceServer.CoverTests;
public sealed class CoverArchiveFallbackTests
{
    private static readonly Guid ReleaseIdentifier = Guid.Parse("e3353cdd-bef4-4e82-a055-38c3db86640f");
    private static readonly Guid GroupIdentifier = Guid.Parse("866e9d8e-200e-334c-8df6-9b8f8384bb22");
    private static readonly Uri MetadataRoot = new("https://metadata.invalid/ws/2/");
    [Theory]
    [InlineData("front")]
    [InlineData("front-500")]
    [InlineData("front-250")]
    public async Task ReleaseWithoutArtworkFallsBackToTheSameTierOnItsReleaseGroup(string tier)
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"release-group\":{{\"id\":\"{GroupIdentifier}\"}}}}") });
        var resolver = new CoverArchiveFallback(() => new HttpClient(handler, false), MetadataRoot, TimeProvider.System);
        var result = await resolver.ResolveAsync(new Uri($"https://coverartarchive.org/release/{ReleaseIdentifier}/{tier}"), CancellationToken.None);
        Assert.Equal($"https://coverartarchive.org/release-group/{GroupIdentifier}/{tier}", result!.AbsoluteUri);
        Assert.Equal("?inc=release-groups&fmt=json", Assert.Single(handler.Addresses).Query);
    }
    [Fact]
    public async Task AllTiersAndConcurrentConsumersShareOneReleaseGroupLookup()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"release-group\":{{\"id\":\"{GroupIdentifier}\"}}}}") });
        var resolver = new CoverArchiveFallback(() => new HttpClient(handler, false), MetadataRoot, TimeProvider.System);
        var results = await Task.WhenAll(new[] { "front", "front-500", "front-250" }.Select(tier =>
            resolver.ResolveAsync(new Uri($"https://coverartarchive.org/release/{ReleaseIdentifier}/{tier}"), CancellationToken.None)));
        Assert.All(results, result => Assert.NotNull(result)); Assert.Single(handler.Addresses);
    }
    [Theory]
    [InlineData("https://unrelated.invalid/release/e3353cdd-bef4-4e82-a055-38c3db86640f/front")]
    [InlineData("https://coverartarchive.org/release-group/866e9d8e-200e-334c-8df6-9b8f8384bb22/front")]
    public async Task UnrelatedAndGroupUrlsNeverTriggerMetadataLookups(string address)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected metadata lookup"));
        var resolver = new CoverArchiveFallback(() => new HttpClient(handler, false), MetadataRoot, TimeProvider.System);
        Assert.Null(await resolver.ResolveAsync(new Uri(address), CancellationToken.None)); Assert.Empty(handler.Addresses);
    }
    [Fact]
    public async Task MissingReleaseGroupsRemainMissingRatherThanInventingAnImage()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var resolver = new CoverArchiveFallback(() => new HttpClient(handler, false), MetadataRoot, TimeProvider.System);
        Assert.Null(await resolver.ResolveAsync(new Uri($"https://coverartarchive.org/release/{ReleaseIdentifier}/front"), CancellationToken.None));
    }
    [Fact]
    public async Task StoreCachesRecoveredArtworkUnderTheOriginalSourceWithoutRepeated404Requests()
    {
        using var fixture = new AssetFixture(); var originals = 0; var alternatives = 0;
        var original = new Uri($"https://coverartarchive.org/release/{ReleaseIdentifier}/front-500");
        var alternative = new Uri($"https://coverartarchive.org/release-group/{GroupIdentifier}/front-500");
        using var handler = new Handler(request => {
            if (request.RequestUri == original) { originals++; return new HttpResponseMessage(HttpStatusCode.NotFound); }
            Assert.Equal(alternative, request.RequestUri); alternatives++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(AssetFixture.Image) };
        });
        using var store = new CoverAssetStore(() => new HttpClient(handler, false), fixture.Renderer, Options.Create(fixture.Options),
            TimeProvider.System, archiveFallback: new Resolver(alternative));
        var first = await store.GetOrCreateAsync(original.AbsoluteUri, CancellationToken.None);
        var second = await store.GetOrCreateAsync(original.AbsoluteUri, CancellationToken.None);
        Assert.Equal(first, second); Assert.Equal(1, originals); Assert.Equal(1, alternatives);
    }
    [Fact]
    public async Task Group404StillReturnsTheRealMissingImageFailureWithoutRecursion()
    {
        using var fixture = new AssetFixture();
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var store = new CoverAssetStore(() => new HttpClient(handler, false), fixture.Renderer, Options.Create(fixture.Options), TimeProvider.System,
            archiveFallback: new Resolver(new Uri($"https://coverartarchive.org/release-group/{GroupIdentifier}/front")));
        await Assert.ThrowsAsync<CoverSourceFailure>(() => store.GetOrCreateAsync($"https://coverartarchive.org/release/{ReleaseIdentifier}/front", CancellationToken.None));
        Assert.Equal(2, handler.Addresses.Count);
    }
    private sealed class Resolver(Uri address) : ICoverArchiveFallback
    {
        public Task<Uri?> ResolveAsync(Uri source, CancellationToken cancellationToken) => Task.FromResult(source.AbsolutePath.StartsWith("/release/", StringComparison.Ordinal) ? address : null);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Addresses { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Addresses.Add(request.RequestUri!); return Task.FromResult(response(request)); }
    }
}
