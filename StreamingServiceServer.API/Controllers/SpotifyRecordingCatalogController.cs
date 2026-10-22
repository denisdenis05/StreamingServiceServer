using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Services.SpotifyCatalog;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = SpotifyCatalogRoutes.AuthenticationScheme, Roles = SpotifyCatalogRoutes.AllowedRoles)]
[Route(SpotifyCatalogRoutes.Root)]
public sealed class SpotifyRecordingCatalogController(IRecordingCatalogQuery catalog) : ControllerBase
{
    [HttpGet(SpotifyCatalogRoutes.Lookup)]
    public async Task<IActionResult> Lookup(Guid identifier, CancellationToken cancellationToken)
    {
        if (identifier == Guid.Empty) return BadRequest(RecordingCatalogPolicy.InvalidSearch);
        var recording = await catalog.Lookup(identifier, cancellationToken);
        return recording is null ? NotFound() : Ok(recording);
    }

    [HttpGet(SpotifyCatalogRoutes.Search)]
    public async Task<IActionResult> Search([FromQuery] string title, [FromQuery] string author,
        CancellationToken cancellationToken, [FromQuery] int limit = RecordingCatalogPolicy.DefaultLimit)
    {
        try { return Ok(await catalog.Search(title, author, limit, cancellationToken)); }
        catch (ArgumentException) { return BadRequest(RecordingCatalogPolicy.InvalidSearch); }
    }
}

public static class SpotifyCatalogRoutes
{
    public const string Root = "spotify/catalog/recordings";
    public const string Lookup = "{identifier:guid}";
    public const string Search = "search";
    public const string AuthenticationScheme = "Bearer";
    public const string AllowedRoles = "User,Admin";
}
