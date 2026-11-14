using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Artists;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class ArtistCatalogTests
{
    private const int FirstOffset = 0;
    private const int OneCall = 1;
    private const int InvalidOffset = -1;
    private const string ArtistName = "Fixture artist";
    private const string AlbumTitle = "Fixture album";
    private const string FixtureCover = "fixture-cover";
    private const string MusicBrainzAddress = "https://musicbrainz.example/ws/2/";
    private const string CoverAddress = "https://covers.example/release/";
    private const string AlbumType = "Album";
    private const string ReleaseDate = "2000";

    [Fact]
    public void ApplicationRegistrationProvidesArtistCatalogAndMemoryCache()
    {
        var services = new ServiceCollection(); services.AddApplicationServices();
        using var container = services.BuildServiceProvider();
        Assert.NotNull(container.GetRequiredService<IMemoryCache>());
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ArtistCatalogService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IArtistCatalogProvider));
    }

    [Fact]
    public async Task CatalogPagesAreCachedAndCanceledRequestsDoNotStartWork()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new Provider(); var service = new ArtistCatalogService(database, provider, new Images(), cache);
        var identifier = Guid.NewGuid();
        var first = await service.GetAsync(identifier, FirstOffset, true, CancellationToken.None);
        Assert.Same(first, await service.GetAsync(identifier, FirstOffset, true, CancellationToken.None));
        Assert.Equal(OneCall, provider.Calls);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(identifier, FirstOffset, true, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ResolveAsync(identifier, null, cancellation.Token));
        Assert.Equal(OneCall, provider.Calls);
    }

    [Fact]
    public async Task FailedCatalogRequestsAreRetriedAndNotCachedAsEmptyPages()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new Provider { Fail = true }; var service = new ArtistCatalogService(database, provider, new Images(), cache);
        var identifier = Guid.NewGuid();
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync(identifier, FirstOffset, true, CancellationToken.None));
        var controller = new ArtistsController(service);
        var unavailable = Assert.IsType<StatusCodeResult>(await controller.Albums(identifier, catalogSource: true));
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        provider.Fail = false;
        Assert.NotNull(await service.GetAsync(identifier, FirstOffset, true, CancellationToken.None));
    }

    [Fact]
    public async Task ControllerRejectsInvalidIdentifiersAndPagingAndRequiresAuthentication()
    {
        var controller = new ArtistsController(null!);
        Assert.IsType<BadRequestResult>(await controller.Resolve(null, null, CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.Resolve(Guid.Empty, Guid.Empty, CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.Albums(Guid.Empty));
        Assert.IsType<BadRequestResult>(await controller.Albums(Guid.NewGuid(), InvalidOffset));
        Assert.IsType<BadRequestResult>(await controller.Albums(Guid.NewGuid(), ArtistCatalogPolicy.MaximumOffset + OneCall));
        Assert.Single(typeof(ArtistsController).GetCustomAttributes(typeof(AuthorizeAttribute), false));
    }

    [Fact]
    public async Task CatalogProviderQueriesExactArtistIdentityAndReturnsNavigableReleaseIds()
    {
        var artistId = Guid.NewGuid(); var releaseId = Guid.NewGuid(); var groupId = Guid.NewGuid();
        var handler = new Responses(artistId, releaseId, groupId);
        using var client = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Music:External:MusicBrainz:BaseUrl"] = MusicBrainzAddress,
            ["Music:External:AlbumCover:BaseUrl"] = CoverAddress
        }).Build();
        IArtistCatalogProvider provider = new MusicBrainzService(client, configuration);
        var page = await provider.GetAlbumsAsync(artistId, FirstOffset, CancellationToken.None);
        var album = Assert.Single(page.Albums);
        Assert.Equal(releaseId, album.Id); Assert.NotEqual(groupId, album.Id);
        Assert.Equal(AlbumTitle, album.Title); Assert.Equal(ArtistName, page.Artist.Name);
        Assert.Equal(ArtistCatalogPolicy.PageSize, page.NextOffset);
        Assert.Equal($"{CoverAddress}{releaseId}/front", album.Cover);
        Assert.Contains($"arid:{artistId}", Uri.UnescapeDataString(handler.Addresses.First().Query));
    }

    [PostgresFact]
    public async Task LibraryAlbumsAreArtistScopedPagedAndDoNotCallExternalServices()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var artist = new Artist { Id = Guid.NewGuid(), Name = ArtistName };
        var releases = Enumerable.Range(FirstOffset, ArtistCatalogPolicy.PageSize + OneCall).Select(index =>
            new Release { Id = Guid.NewGuid(), Title = $"{AlbumTitle} {index:D3}", Artist = artist, Cover = FixtureCover }).ToArray();
        database.Releases.AddRange(releases);
        database.Releases.Add(new Release { Id = Guid.NewGuid(), Title = AlbumTitle, Cover = FixtureCover,
            Artist = new Artist { Id = Guid.NewGuid(), Name = ArtistName } });
        var recording = new Recording { Id = Guid.NewGuid(), Title = AlbumTitle, Release = releases.First(), Cover = FixtureCover };
        database.Recordings.Add(recording); await database.SaveChangesAsync(); database.ChangeTracker.Clear();
        using var cache = new MemoryCache(new MemoryCacheOptions()); var provider = new Provider();
        var service = new ArtistCatalogService(database, provider, new Images(), cache);
        var page = await service.GetAsync(artist.Id, FirstOffset, false, CancellationToken.None);
        Assert.Equal(ArtistCatalogPolicy.PageSize, page!.Albums.Count);
        Assert.Equal(ArtistCatalogPolicy.PageSize, page.NextOffset);
        var last = await service.GetAsync(artist.Id, Assert.IsType<int>(page.NextOffset), false, CancellationToken.None);
        Assert.Single(last!.Albums); Assert.Null(last.NextOffset);
        Assert.Equal(artist.Id, (await service.ResolveAsync(recording.Id, null, CancellationToken.None))!.Id);
        Assert.Equal(artist.Id, (await service.ResolveAsync(null, releases.First().Id, CancellationToken.None))!.Id);
        Assert.Equal(FirstOffset, provider.Calls); Assert.Empty(database.ChangeTracker.Entries());
    }

    private sealed class Provider : IArtistCatalogProvider
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<ArtistResponse?> GetArtistAsync(Guid artistId, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult<ArtistResponse?>(new ArtistResponse { Id = artistId, Name = ArtistName }); }
        public Task<ArtistResponse?> ResolveArtistAsync(Guid? recordingId, Guid? albumId, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult<ArtistResponse?>(null); }
        public Task<ArtistAlbumPage> GetAlbumsAsync(Guid artistId, int offset, CancellationToken cancellationToken)
        {
            Calls++; if (Fail) throw new HttpRequestException();
            return Task.FromResult(new ArtistAlbumPage(new ArtistResponse { Id = artistId, Name = ArtistName }, [], null));
        }
    }
    private sealed class Images : IImageService
    {
        public string GetPreferredCover(string? local, string? optimized, string? original) => local ?? optimized ?? original ?? string.Empty;
        public AlbumCoversDto GetAlbumCover(Guid releaseId, Guid? releaseGroupId = null) => new();
        public AlbumCoversDto GetAllAlbumCovers(Guid releaseGroupId) => new();
    }
    private sealed class Responses(Guid artistId, Guid releaseId, Guid groupId) : HttpMessageHandler
    {
        public List<Uri> Addresses { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Addresses.Add(request.RequestUri!);
            var body = request.RequestUri!.AbsolutePath.Contains("release-group")
                ? JsonSerializer.Serialize(new Dictionary<string, object> {
                    ["count"] = ArtistCatalogPolicy.PageSize + OneCall,
                    ["release-groups"] = new[] { new { id = groupId, title = AlbumTitle,
                        releases = new[] { new { id = releaseId, date = ReleaseDate } } } }
                }) : JsonSerializer.Serialize(new { id = artistId, name = ArtistName });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
