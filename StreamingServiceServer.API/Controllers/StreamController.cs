using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Models.LastFm;
using StreamingServiceServer.Business.Services.LastFm;
using StreamingServiceServer.Business.Services.MusicSearch;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer", Roles = "User,Admin")]
[Route("[controller]")]
public class StreamController : ControllerBase
{
    private readonly IStreamingService _streamingService;
    private readonly ILastFmService _lastFmService;
    
    public StreamController(IStreamingService streamingService, ILastFmService lastFmService)
    {
        _streamingService = streamingService;
        _lastFmService = lastFmService;
    }
    
    [HttpGet]
    public async Task<IActionResult> StreamAudio([FromQuery] Guid id)
    {
        var trackPath = await _streamingService.GetStreamingPath(id);
        
        var encodedSegments = trackPath
            .Split('/')
            .Select(Uri.EscapeDataString);

        var encodedPath = string.Join("/", encodedSegments);
        
        var internalPath = $"/protected/{encodedPath}";

        Response.Headers["X-Accel-Redirect"] = internalPath;
        return new EmptyResult();

    }
    
    [HttpPost("start-playback")]
    public async Task<IActionResult> StartPlayback([FromBody] StartPlaybackRequest request)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        try
        {
            var sessionId = await _lastFmService.StartPlaybackSession(userId, request);
            
            return Ok(new { 
                success = true, 
                sessionId = sessionId,
                message = "Playback session started" 
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, "Failed to start playback session");
        }
    }

    [HttpPost("stop-playback")]
    public async Task<IActionResult> StopPlayback([FromBody] StopPlaybackRequest request)
    {
        try
        {
            await _lastFmService.StopPlaybackSession(request.SessionId, request.PlayedSeconds);
            
            return Ok(new { 
                success = true, 
                message = "Playback session stopped" 
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, "Failed to stop playback session");
        }
    }

    [HttpPost("playback-progress")]
    public async Task<IActionResult> UpdatePlaybackProgress([FromBody] PlaybackProgressRequest request)
    {
        try
        {
            await _lastFmService.UpdatePlaybackProgress(request.SessionId, request.PlayedSeconds);
            
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, "Failed to update playback progress");
        }
    }
}