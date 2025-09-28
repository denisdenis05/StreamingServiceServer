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
}