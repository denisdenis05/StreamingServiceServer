using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialMedia.Data.Models;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images.Playlists;
using StreamingServiceServer.Business.Services.Recommendations;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class RecommendationPlaylistPreviewTests
{
    [Fact]
    public async Task HomeCollectionsContainTheExactTracksAndCollageWithoutSaving()
    {
        var recommendations = new Recommendations();
        var artwork = new Artwork();
        var controller = Controller(recommendations, artwork);
        var response = Assert.IsType<OkObjectResult>(await controller.Collections(CancellationToken.None));
        var cards = JsonSerializer.SerializeToElement(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(RecommendationPlaylistCatalog.Cards.Count, cards.GetArrayLength());
        Assert.Equal("Recommendation", cards[0].GetProperty("type").GetString());
        Assert.Equal(Artwork.Cover, cards[0].GetProperty("cover").GetString());
        var daily = cards[0].GetProperty("recommendation");
        Assert.Equal(recommendations.Version, daily.GetProperty("dailyVersion").GetGuid());
        Assert.Equal(recommendations.Tracks[0].Id, daily.GetProperty("page").GetProperty("tracks")[0].GetProperty("id").GetGuid());
        Assert.Equal(RecommendationPolicy.SmartShuffle, cards[1].GetProperty("recommendation").GetProperty("request").GetProperty("mode").GetString());
        Assert.Equal(RecommendationPlaylistCatalog.Cards.Count, artwork.Calls);
        Assert.Equal(recommendations.Tracks.Select(track => track.Cover), artwork.Covers);
        Assert.Equal(0, recommendations.Saves);
    }

    [Fact]
    public async Task SeededRadioPreviewPreservesTheRequestAndUsesTheSameArtworkService()
    {
        var recommendations = new Recommendations();
        var artwork = new Artwork();
        var request = new RecommendationRequest(RecommendationPolicy.SongRadio, Guid.NewGuid());
        var response = Assert.IsType<OkObjectResult>(await Controller(recommendations, artwork).Preview(request, CancellationToken.None));
        var preview = Assert.IsType<RecommendationPlaylistPreview>(response.Value);
        Assert.Equal(request, preview.Request);
        Assert.Equal(request, recommendations.LastRequest);
        Assert.Equal(recommendations.Tracks, preview.Page.Tracks);
        Assert.Equal(Artwork.Cover, preview.Cover);
        Assert.Null(preview.DailyVersion);
        Assert.Equal(0, recommendations.Saves);
    }

    [Fact]
    public async Task ExplicitPreviewRefreshAndDailyRegenerationReachTheCache()
    {
        var recommendations = new Recommendations();
        var cache = new Cache();
        var controller = Controller(recommendations, new Artwork(), cache);
        await controller.Preview(new RecommendationRequest(RecommendationPolicy.Calm), CancellationToken.None, refresh: true);
        Assert.True(cache.Refresh);
        await controller.Regenerate(new RegenerateDailyMix(recommendations.Version), CancellationToken.None);
        Assert.Equal(1, cache.Invalidations);
    }

    [PostgresFact]
    public async Task RepeatedHomeFetchesReuseEveryGeneratedMixAndCover()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = new User { Id = Guid.NewGuid(), Username = "Home cache fixture", Email = "home@fixture.invalid", PasswordHash = "fixture", ProviderId = "fixture" };
        database.Users.Add(owner);
        await database.SaveChangesAsync();
        var recommendations = new Recommendations();
        var artwork = new Artwork();
        var cache = new RecommendationPreviewCache(database, fixture.Clock, new LibraryService(database, null!, null!), null!);
        var controller = Controller(recommendations, artwork, cache, owner.Id);
        await controller.Collections(CancellationToken.None);
        await controller.Collections(CancellationToken.None);
        Assert.Equal(RecommendationPlaylistCatalog.Cards.Count, artwork.Calls);
        Assert.Equal(RecommendationPlaylistCatalog.Cards.Count - 1, recommendations.Generations);
        Assert.Equal(1, recommendations.DailyLoads);
        Assert.Equal(RecommendationPlaylistCatalog.Cards.Count, await database.RecommendationPlaylistCaches.CountAsync());
    }

    private static AudioRecommendationsController Controller(Recommendations recommendations, Artwork artwork, IRecommendationPreviewCache? cache = null, Guid? userId = null) => new(recommendations, artwork, cache ?? new Cache())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString())], "fixture")) } }
    };
    private sealed class Cache : IRecommendationPreviewCache
    {
        public bool Refresh { get; private set; }
        public int Invalidations { get; private set; }
        public Task<RecommendationPlaylistPreview> GetAsync(Guid userId, RecommendationRequest request, Func<CancellationToken, Task<RecommendationPlaylistPreview>> generate, bool refresh, CancellationToken cancellationToken)
        { Refresh = refresh; return generate(cancellationToken); }
        public Task InvalidateDailyAsync(Guid userId, CancellationToken cancellationToken) { Invalidations++; return Task.CompletedTask; }
        public Task DeleteExpiredAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class Artwork : IPlaylistArtworkService
    {
        public const string Cover = "https://fixture.invalid/playlist-preview.jpg";
        public int Calls { get; private set; }
        public IReadOnlyList<string?> Covers { get; private set; } = [];
        public Task<string> PreviewAsync(IReadOnlyList<string?> covers, CancellationToken cancellationToken)
        { Calls++; Covers = covers; return Task.FromResult(Cover); }
        public Task<string> RefreshAsync(Guid userId, Guid playlistId, CancellationToken cancellationToken) => throw new InvalidOperationException("Preview must not require a saved playlist.");
    }
    private sealed class Recommendations : IAudioRecommendations
    {
        public Guid Version { get; } = Guid.NewGuid();
        public IReadOnlyList<RecordingResponse> Tracks { get; } = [
            new() { Id = Guid.NewGuid(), Title = "First", Cover = "https://fixture.invalid/first.jpg" },
            new() { Id = Guid.NewGuid(), Title = "Second", Cover = "https://fixture.invalid/second.jpg" } ];
        public int Saves { get; private set; }
        public int Generations { get; private set; }
        public int DailyLoads { get; private set; }
        public RecommendationRequest? LastRequest { get; private set; }
        public Task<RecommendationOverview> OverviewAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Collection requests must not reload feature analysis just to get their titles.");
        public Task<RecommendationPage> GenerateAsync(Guid userId, RecommendationRequest request, CancellationToken cancellationToken)
        { Generations++; LastRequest = request; return Task.FromResult(new RecommendationPage(Tracks, null, "Similar tracks", "fixture", request.Mode)); }
        public Task<DailyMixResponse> DailyAsync(Guid userId, Guid? expectedVersion, CancellationToken cancellationToken)
        { DailyLoads++; return Task.FromResult(new DailyMixResponse(Version, new DateOnly(2026, 11, 21), Tracks, null)); }
        public Task<PlaylistResponse> SaveAsync(Guid userId, SaveRecommendationPlaylist request, CancellationToken cancellationToken)
        { Saves++; throw new InvalidOperationException("Preview must not save a playlist."); }
    }
}
