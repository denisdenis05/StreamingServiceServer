using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Services.Artists;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer", Roles = "User,Admin")]
[Route("api/artists")]
public sealed class ArtistsController(ArtistCatalogService catalog) : ControllerBase
{
    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve([FromQuery] Guid? recordingId, [FromQuery] Guid? albumId, CancellationToken cancellationToken)
    {
        if ((!recordingId.HasValue || recordingId == Guid.Empty) && (!albumId.HasValue || albumId == Guid.Empty)) return BadRequest();
        try
        {
            var artist = await catalog.ResolveAsync(recordingId, albumId, cancellationToken);
            return artist == null ? NotFound() : Ok(artist);
        }
        catch (HttpRequestException) { return StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    [HttpGet("{artistId:guid}/albums")]
    public async Task<IActionResult> Albums(Guid artistId, [FromQuery] int offset = ArtistCatalogPolicy.FirstOffset,
        [FromQuery] bool catalogSource = false, CancellationToken cancellationToken = default)
    {
        if (artistId == Guid.Empty || offset < ArtistCatalogPolicy.FirstOffset || offset > ArtistCatalogPolicy.MaximumOffset) return BadRequest();
        try
        {
            var page = await catalog.GetAsync(artistId, offset, catalogSource, cancellationToken);
            return page == null ? NotFound() : Ok(page);
        }
        catch (HttpRequestException) { return StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
}
