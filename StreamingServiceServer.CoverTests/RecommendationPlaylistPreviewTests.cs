using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images.Playlists;
using StreamingServiceServer.Business.Services.Recommendations;
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
        Assert.Equal(2, cards.GetArrayLength());
        Assert.Equal("Recommendation", cards[0].GetProperty("type").GetString());
        Assert.Equal(Artwork.Cover, cards[0].GetProperty("cover").GetString());
        var daily = cards[0].GetProperty("recommendation");
        Assert.Equal(recommendations.Version, daily.GetProperty("dailyVersion").GetGuid());
        Assert.Equal(recommendations.Tracks[0].Id, daily.GetProperty("page").GetProperty("tracks")[0].GetProperty("id").GetGuid());
        Assert.Equal(RecommendationPolicy.Calm, cards[1].GetProperty("recommendation").GetProperty("request").GetProperty("mode").GetString());
        Assert.Equal(2, artwork.Calls);
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

    private static AudioRecommendationsController Controller(Recommendations recommendations, Artwork artwork) => new(recommendations, artwork)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "fixture")) } }
    };
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
        public RecommendationRequest? LastRequest { get; private set; }
        public Task<RecommendationOverview> OverviewAsync(CancellationToken cancellationToken) => Task.FromResult(new RecommendationOverview(2,
            [new(RecommendationPolicy.Daily, "Today's playlist", "Daily tracks"), new(RecommendationPolicy.Calm, "Take it easy", "Calmer tracks")]));
        public Task<RecommendationPage> GenerateAsync(Guid userId, RecommendationRequest request, CancellationToken cancellationToken)
        { LastRequest = request; return Task.FromResult(new RecommendationPage(Tracks, "cursor", "Similar tracks", "fixture", request.Mode)); }
        public Task<DailyMixResponse> DailyAsync(Guid userId, Guid? expectedVersion, CancellationToken cancellationToken) => Task.FromResult(new DailyMixResponse(Version, new DateOnly(2026, 11, 21), Tracks, null));
        public Task<PlaylistResponse> SaveAsync(Guid userId, SaveRecommendationPlaylist request, CancellationToken cancellationToken)
        { Saves++; throw new InvalidOperationException("Preview must not save a playlist."); }
    }
}
