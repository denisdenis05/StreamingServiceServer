using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Services.Recommendations;
using StreamingServiceServer.Business.Services.Images.Playlists;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer", Roles = "User,Admin")]
[Route(RecommendationPolicy.Route)]
public sealed class AudioRecommendationsController(IAudioRecommendations recommendations, IPlaylistArtworkService artwork) : ControllerBase
{
    private const string InvalidAccount = "Sign in again to use recommendations.";
    [HttpGet("overview")]
    public Task<IActionResult> Overview(CancellationToken cancellationToken) => RunAsync(async userId => await recommendations.OverviewAsync(cancellationToken));
    [HttpGet("collections")]
    public Task<IActionResult> Collections(CancellationToken cancellationToken) => RunAsync(async userId =>
    {
        var overview = await recommendations.OverviewAsync(cancellationToken);
        var cards = new List<object>();
        foreach (var card in overview.Cards)
        {
            var preview = await PreviewAsync(userId, new RecommendationRequest(card.Mode), cancellationToken);
            cards.Add(new { id = card.Mode, title = card.Title, artistName = card.Description,
                cover = preview.Cover, type = "Recommendation", recommendation = preview });
        }
        return cards;
    });
    [HttpPost("preview")]
    public Task<IActionResult> Preview(RecommendationRequest request, CancellationToken cancellationToken) =>
        RunAsync(async userId => await PreviewAsync(userId, request, cancellationToken));
    [HttpPost("generate")]
    public Task<IActionResult> Generate(RecommendationRequest request, CancellationToken cancellationToken) =>
        RunAsync(async userId => await recommendations.GenerateAsync(userId, request, cancellationToken));
    [HttpGet("daily")]
    public Task<IActionResult> Daily(CancellationToken cancellationToken) => RunAsync(async userId => await recommendations.DailyAsync(userId, null, cancellationToken));
    [HttpPost("daily/regenerate")]
    public Task<IActionResult> Regenerate(RegenerateDailyMix request, CancellationToken cancellationToken) =>
        RunAsync(async userId => await recommendations.DailyAsync(userId, request.ExpectedVersion, cancellationToken));
    [HttpPost("playlists")]
    public Task<IActionResult> Save(SaveRecommendationPlaylist request, CancellationToken cancellationToken) =>
        RunAsync(async userId => await recommendations.SaveAsync(userId, request, cancellationToken));
    private async Task<RecommendationPlaylistPreview> PreviewAsync(Guid userId, RecommendationRequest request, CancellationToken cancellationToken)
    {
        DailyMixResponse? daily = request.Mode == RecommendationPolicy.Daily
            ? await recommendations.DailyAsync(userId, null, cancellationToken) : null;
        var page = daily is null ? await recommendations.GenerateAsync(userId, request, cancellationToken)
            : new RecommendationPage(daily.Tracks, null, "Your daily selection.", daily.Version.ToString(), request.Mode);
        var cover = await artwork.PreviewAsync(page.Tracks.Select(track => track.Cover).ToArray(), cancellationToken);
        return new RecommendationPlaylistPreview(request, page, cover, daily?.Version, daily?.SavedPlaylistId);
    }
    private async Task<IActionResult> RunAsync(Func<Guid, Task<object>> action)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId == Guid.Empty) return Unauthorized(InvalidAccount);
        try { return Ok(await action(userId)); }
        catch (RecommendationInputException failure) { return BadRequest(new { message = failure.Message, code = failure.FailureCode }); }
        catch (RecommendationConflictException failure) { return Conflict(new { message = failure.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
