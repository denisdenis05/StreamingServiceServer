using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Services.Recommendation;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer", Roles = "User,Admin")]
[Route("[controller]")]
public class RecommendationController : ControllerBase
{
    private readonly IRecommendationService _recommendationService;

    public RecommendationController(IRecommendationService recommendationService)
    {
        _recommendationService = recommendationService;
    }

    [HttpGet("sections")]
    public async Task<IActionResult> GetSections()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        var sections = await _recommendationService.GetSections(userId);
        return Ok(sections);
    }

    [HttpGet("recent-quick")]
    public async Task<IActionResult> GetRecentQuick([FromQuery] int count = 6)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        var items = await _recommendationService.GetRecentQuick(userId, Math.Clamp(count, 1, 12));
        return Ok(items);
    }

    [HttpGet("section/{id}")]
    public async Task<IActionResult> GetSection(string id)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        var section = await _recommendationService.GetSection(id, userId);
        return Ok(section);
    }
}
