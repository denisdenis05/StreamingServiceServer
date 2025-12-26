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
        var sections = await _recommendationService.GetSections();
        return Ok(sections);
    }

    [HttpGet("section/{id}")]
    public async Task<IActionResult> GetSection(string id)
    {
        var section = await _recommendationService.GetSection(id);
        return Ok(section);
    }
}
