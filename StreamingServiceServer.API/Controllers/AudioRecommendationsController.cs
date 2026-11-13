using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Services.Recommendations;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer", Roles = "User,Admin")]
[Route(RecommendationPolicy.Route)]
public sealed class AudioRecommendationsController(IAudioRecommendations recommendations) : ControllerBase
{
    private const string InvalidAccount = "Sign in again to use recommendations.";
    [HttpGet("overview")]
    public Task<IActionResult> Overview(CancellationToken cancellationToken) => RunAsync(async userId => await recommendations.OverviewAsync(cancellationToken));
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
    private async Task<IActionResult> RunAsync(Func<Guid, Task<object>> action)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId == Guid.Empty) return Unauthorized(InvalidAccount);
        try { return Ok(await action(userId)); }
        catch (RecommendationInputException failure) { return BadRequest(new { message = failure.Message, code = failure.FailureCode }); }
        catch (RecommendationConflictException failure) { return Conflict(new { message = failure.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
