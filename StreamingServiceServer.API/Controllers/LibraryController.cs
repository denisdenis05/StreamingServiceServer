using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Services.MusicSearch;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer", Roles = "User,Admin")]
[Route("[controller]")]
public class LibraryController : ControllerBase
{
    private readonly ILibraryService _libraryService;

    public LibraryController(ILibraryService libraryService)
    {
        _libraryService = libraryService;
    }

    [HttpPost("playlists")]
    public async Task<IActionResult> CreatePlaylist([FromBody] RecordingIdList request, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
 
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        var playlist = await _libraryService.CreatePlaylistAsync(userId, request, cancellationToken);

        return Ok();
    }
    
    [HttpGet("playlists")]
    public async Task<IActionResult> GetUserPlaylists(CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        var playlists = await _libraryService.GetUserPlaylistsAsync(userId, cancellationToken);
        return Ok(playlists);
    }

    [HttpGet("playlists/{id:guid}/recordings")]
    public async Task<IActionResult> GetPlaylistRecordings(Guid id, CancellationToken cancellationToken)
    {
        var recordings = await _libraryService.GetPlaylistRecordingsAsync(id, cancellationToken);
        return Ok(recordings);
    }
}