using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Models.LastFm;
using StreamingServiceServer.Business.Services.LastFm;
using StreamingServiceServer.Data;

[ApiController]
[Route("[controller]")]
public class LastFmController : ControllerBase
{
    private readonly ILastFmService _lastFmService;
    private readonly ILogger<LastFmController> _logger;
    private readonly StreamingDbContext _dbContext; 

    public LastFmController(
        ILastFmService lastFmService,
        ILogger<LastFmController> logger,
        StreamingDbContext dbContext)
    {
        _lastFmService = lastFmService;
        _logger = logger;
        _dbContext = dbContext;
    }

    [HttpGet("auth-url")]
    public async Task<IActionResult> GetAuthUrl()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
 
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }
        try
        {
            var authUrl = await _lastFmService.GenerateAuthUrl();

            return Ok(new { authUrl, callbackUrl = $"http://localhost:5068/lastfm/callback?userId={userId}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating Last.fm auth URL");
            return StatusCode(500, "Failed to generate auth URL");
        }
    }

    [AllowAnonymous]
    [HttpGet("callback")]
    public IActionResult HandleCallback([FromQuery] string token)
    {
        try
        {
            if (string.IsNullOrEmpty(token))
            {
                return Redirect($"streamingservice://lastfm/callback?error=missing_token");
            }

            return Redirect($"streamingservice://lastfm/callback?token={token}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Last.fm callback");
            
            return Redirect($"streamingservice://lastfm/callback?error=server_error");
        }
    }

    [HttpPost("connect")]
    public async Task<IActionResult> ConnectAccount([FromBody] LastFmAuthRequest request)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
 
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }
        try
        {
            var (sessionKey, username) = await _lastFmService.GetSessionKeyAndUsername(request.Token);

            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null)
            {
                return BadRequest("User not found");
            }

            user.LastFmSessionKey = sessionKey;
            user.LastFmUsername = username;
            user.LastFmConnectedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();

            return Ok(new { success = true, message = "Last.fm account connected successfully", username });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting Last.fm account");
            return StatusCode(500, "Failed to connect Last.fm account");
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetConnectionStatus()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
 
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }
        
        try
        {
            var user = await _dbContext.Users.FindAsync(userId);

            if (user == null)
            {
                return BadRequest("User not found");
            }

            return Ok(new
            {
                connected = !string.IsNullOrEmpty(user.LastFmSessionKey),
                username = user.LastFmUsername,
                connectedAt = user.LastFmConnectedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Last.fm connection status");
            return StatusCode(500, "Failed to get connection status");
        }
    }

    [HttpDelete("disconnect")]
    public async Task<IActionResult> DisconnectAccount()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
 
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized("Invalid or missing user ID in token.");
        }

        try
        {
            var user = await _dbContext.Users.FindAsync(userId);

            if (user == null)
            {
                return BadRequest("User not found");
            }

            user.LastFmSessionKey = null;
            user.LastFmUsername = null;
            user.LastFmConnectedAt = null;

            await _dbContext.SaveChangesAsync();

            return Ok(new { success = true, message = "Last.fm account disconnected successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting Last.fm account");
            return StatusCode(500, "Failed to disconnect Last.fm account");
        }
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
            
            _logger.LogInformation("Started playback session {SessionId} for user {UserId}, track {TrackId}", 
                sessionId, userId, request.TrackId);
            
            return Ok(new { 
                success = true, 
                sessionId = sessionId,
                message = "Playback session started" 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting playback session for user {UserId}", userId);
            return StatusCode(500, "Failed to start playback session");
        }
    }

    [HttpPost("stop-playback")]
    public async Task<IActionResult> StopPlayback([FromBody] StopPlaybackRequest request)
    {
        try
        {
            await _lastFmService.StopPlaybackSession(request.SessionId, request.PlayedSeconds);
            
            _logger.LogInformation("Stopped playback session {SessionId}", request.SessionId);
            
            return Ok(new { 
                success = true, 
                message = "Playback session stopped" 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping playback session {SessionId}", request.SessionId);
            return StatusCode(500, "Failed to stop playback session");
        }
    }

    [HttpPost("playback-progress")]
    public async Task<IActionResult> UpdatePlaybackProgress([FromBody] PlaybackProgressRequest request)
    {
        try
        {
            await _lastFmService.UpdatePlaybackProgress(request.SessionId, request.PlayedSeconds);
            
            if (request.Events?.Any() == true)
            {
                foreach (var playbackEvent in request.Events)
                {
                    await _lastFmService.RecordPlaybackEvent(request.SessionId, playbackEvent);
                }
            }
            
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating playback progress for session {SessionId}", request.SessionId);
            return StatusCode(500, "Failed to update playback progress");
        }
    }

    [HttpPost("pause")]
    public async Task<IActionResult> PausePlayback([FromBody] PausePlaybackRequest request)
    {
        try
        {
            var playbackEvent = new PlaybackEvent
            {
                Timestamp = DateTime.UtcNow,
                EventType = "pause"
            };

            await _lastFmService.RecordPlaybackEvent(request.SessionId, playbackEvent);
            await _lastFmService.UpdatePlaybackProgress(request.SessionId, request.PlayedSeconds);
            
            return Ok(new { success = true, message = "Playback paused" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing playback for session {SessionId}", request.SessionId);
            return StatusCode(500, "Failed to pause playback");
        }
    }

    [HttpPost("resume")]
    public async Task<IActionResult> ResumePlayback([FromBody] ResumePlaybackRequest request)
    {
        try
        {
            var playbackEvent = new PlaybackEvent
            {
                Timestamp = DateTime.UtcNow,
                EventType = "resume"
            };

            await _lastFmService.RecordPlaybackEvent(request.SessionId, playbackEvent);
            
            return Ok(new { success = true, message = "Playback resumed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming playback for session {SessionId}", request.SessionId);
            return StatusCode(500, "Failed to resume playback");
        }
    }

    [HttpPost("seek")]
    public async Task<IActionResult> SeekPlayback([FromBody] SeekPlaybackRequest request)
    {
        try
        {
            var playbackEvent = new PlaybackEvent
            {
                Timestamp = DateTime.UtcNow,
                EventType = "seek",
                SeekFromSeconds = request.FromSeconds,
                SeekToSeconds = request.ToSeconds
            };

            await _lastFmService.RecordPlaybackEvent(request.SessionId, playbackEvent);
            await _lastFmService.UpdatePlaybackProgress(request.SessionId, request.ToSeconds);
            
            return Ok(new { success = true, message = "Seek recorded" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording seek for session {SessionId}", request.SessionId);
            return StatusCode(500, "Failed to record seek");
        }
    }
}