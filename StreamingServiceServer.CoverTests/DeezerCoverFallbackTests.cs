using System.Net;
using System.Text.Json;
using StreamingServiceServer.Business.Services.Images.Assets;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class DeezerCoverFallbackTests
{
    private const string Image = "https://cdn-images.dzcdn.net/images/cover/example/1000.jpg";

    [Fact]
    public async Task MatchesArtistAndAlbumAndSharesLookupAcrossConcurrentConsumers()
    {
        using var handler = new Handler(_ => Reply("Artist", "Album", Image));
        using var provider = new DeezerCoverFallback(() => new HttpClient(handler, false), TimeProvider.System);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => provider.ResolveAsync(" artist ", "ALBUM", CancellationToken.None)));
        Assert.All(results, result => Assert.Equal(Image, result!.AbsoluteUri));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("Other artist", "Album", Image)]
    [InlineData("Artist", "Other album", Image)]
    [InlineData("Artist", "Album (Deluxe)", Image)]
    [InlineData("Artist", "Album", "http://cdn-images.dzcdn.net/cover.jpg")]
    [InlineData("Artist", "Album", "https://dzcdn.net.evil.invalid/cover.jpg")]
    [InlineData("Artist", "Album", "https://unrelated.invalid/cover.jpg")]
    public async Task RejectsDifferentAlbumsArtistsAndUntrustedArtwork(string artist, string album, string image)
    {
        using var handler = new Handler(_ => Reply(artist, album, image));
        using var provider = new DeezerCoverFallback(() => new HttpClient(handler, false), TimeProvider.System);
        Assert.Null(await provider.ResolveAsync("Artist", "Album", CancellationToken.None));
        Assert.Null(await provider.ResolveAsync("Artist", "Album", CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("", "Album")]
    [InlineData("Artist", " ")]
    public async Task MissingMetadataDoesNotSearch(string artist, string album)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException());
        using var provider = new DeezerCoverFallback(() => new HttpClient(handler, false), TimeProvider.System);
        Assert.Null(await provider.ResolveAsync(artist, album, CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task ProviderErrorsAreNotCachedAsMissingArtwork()
    {
        using var handler = new Handler(attempt => attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"error\":{\"code\":4}}") }
            : Reply("Artist", "Album", Image));
        using var provider = new DeezerCoverFallback(() => new HttpClient(handler, false), TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => provider.ResolveAsync("Artist", "Album", CancellationToken.None));
        Assert.Equal(Image, (await provider.ResolveAsync("Artist", "Album", CancellationToken.None))!.AbsoluteUri);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task CancellationStopsLookup()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException());
        using var provider = new DeezerCoverFallback(() => new HttpClient(handler, false), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ResolveAsync("Artist", "Album", cancellation.Token));
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"data\":null}")]
    public async Task MalformedResponsesRemainRetryable(string json)
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        using var provider = new DeezerCoverFallback(() => new HttpClient(handler, false), TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => provider.ResolveAsync("Artist", "Album", CancellationToken.None));
    }

    private static HttpResponseMessage Reply(string artist, string album, string image) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { title = album, artist = new { name = artist }, cover_xl = image } } })) };

    private sealed class Handler(Func<int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.deezer.com", request.RequestUri!.Host);
            return Task.FromResult(reply(++Requests));
        }
    }
}
