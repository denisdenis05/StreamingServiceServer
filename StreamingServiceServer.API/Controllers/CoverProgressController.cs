using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Services.Images.Progress;
namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/dashboard/covers")]
public sealed class CoverProgressController(CoverDownloadProgress progress) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CoverProgressPage>> Get(
        [FromQuery, Range(CoverProgressPolicy.FirstPage, CoverProgressPolicy.MaximumPage)] int page = CoverProgressPolicy.FirstPage,
        [FromQuery, Range(1, CoverProgressPolicy.MaximumPageSize)] int pageSize = CoverProgressPolicy.DefaultPageSize,
        [FromQuery] CoverProgressFilter filter = CoverProgressFilter.All,
        [FromQuery, StringLength(CoverProgressPolicy.MaximumSearchLength)] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(filter)) return BadRequest(new { message = "Unknown cover filter." });
        return Ok(await progress.ReadAsync(page, pageSize, filter, search, cancellationToken));
    }
}
