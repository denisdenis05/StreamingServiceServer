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
    public async Task<IActionResult> CreatePlaylist([FromBody] CreatePlaylistRequest request, CancellationToken cancellationToken)
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
    public async Task<IActionResult> GetPlaylistRecordings(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        var recordings = await _libraryService.GetPlaylistRecordingsAsync(id, userId, page, pageSize, cancellationToken);
        return Ok(recordings);
    }
    [HttpGet]
    public async Task<IActionResult> GetLibrary(CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        var library = await _libraryService.GetLibraryAsync(userId, cancellationToken);
        return Ok(library);
    }

    [HttpDelete("playlists/{playlistId}/recordings/{recordingId}")]
    public async Task<IActionResult> RemoveRecordingFromPlaylist(Guid playlistId, Guid recordingId, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        await _libraryService.RemoveRecordingFromPlaylistAsync(userId, playlistId, recordingId, cancellationToken);
        return Ok();
    }

    [HttpPut("playlists/{playlistId}")]
    public async Task<IActionResult> UpdatePlaylist(Guid playlistId, [FromBody] UpdatePlaylistRequest request, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        await _libraryService.UpdatePlaylistAsync(userId, playlistId, request.Name, cancellationToken);
        return Ok();
    }

    [HttpPost("playlists/{playlistId}/recordings")]
    public async Task<IActionResult> AddRecordingsToPlaylist(Guid playlistId, [FromBody] List<AddSongToPlaylistRequest> songs, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        await _libraryService.AddRecordingsToPlaylistAsync(userId, playlistId, songs, cancellationToken);
        return Ok();
    }
    [HttpPost("likes/{recordingId}")]
    public async Task<IActionResult> ToggleLike(Guid recordingId, [FromBody] AddSongToPlaylistRequest songDetails, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        await _libraryService.ToggleLikeAsync(userId, recordingId, songDetails, cancellationToken);
        return Ok();
    }

    [HttpPost("playlists/{playlistId}/recordings/fuzzy")]
    public async Task<IActionResult> AddRecordingsToPlaylistFuzzy(Guid playlistId, [FromBody] List<AddRecordingToPlaylistFuzzyRequest> requests, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        await _libraryService.AddRecordingsToPlaylistFuzzyAsync(userId, playlistId, requests, cancellationToken);
        return Ok();
    }
}
