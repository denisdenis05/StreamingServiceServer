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

    /// <summary>
    /// Import a playlist from a JSON array of track objects (Spotify-like).
    /// Each item: { "name": "...", "artistName": "...", "releaseName": "..." }
    /// Returns per-track match results so the client can show matched vs unavailable.
    /// </summary>
    [HttpPost("playlists/{playlistId}/import-json")]
    public async Task<IActionResult> ImportPlaylistFromJson(Guid playlistId, [FromBody] List<AddRecordingToPlaylistFuzzyRequest> tracks, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("Invalid or missing user ID in token.");

        if (tracks == null || tracks.Count == 0)
            return BadRequest("Track list is empty.");

        var results = await _libraryService.ImportPlaylistFromJsonAsync(userId, playlistId, tracks, cancellationToken);
        return Ok(results);
    }

    /// <summary>
    /// Replace a single recording in a playlist (swap an unavailable / wrong match with a correct one).
    /// Body: { "id": "recording-guid", "title": "...", "artist": "...", "album": "...", "cover": "..." }
    /// </summary>
    [HttpPut("playlists/{playlistId}/recordings/{recordingId}/replace")]
    public async Task<IActionResult> ReplacePlaylistRecording(Guid playlistId, Guid recordingId, [FromBody] AddSongToPlaylistRequest newSong, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("Invalid or missing user ID in token.");

        await _libraryService.ReplacePlaylistRecordingAsync(userId, playlistId, recordingId, newSong, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// Create an empty playlist (to receive an import). Returns the playlist ID.
    /// </summary>
    [HttpPost("playlists/create-for-import")]
    public async Task<IActionResult> CreatePlaylistForImport([FromBody] CreatePlaylistRequest request, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("Invalid or missing user ID in token.");

        var playlist = await _libraryService.CreatePlaylistAsync(userId, request, cancellationToken);
        return Ok(new { id = playlist.Id, name = playlist.Name });
    }

    /// <summary>
    /// Delete a playlist and all its recordings entries.
    /// </summary>
    [HttpDelete("playlists/{playlistId}")]
    public async Task<IActionResult> DeletePlaylist(Guid playlistId, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("Invalid or missing user ID in token.");

        await _libraryService.DeletePlaylistAsync(userId, playlistId, cancellationToken);
        return Ok();
    }
}
