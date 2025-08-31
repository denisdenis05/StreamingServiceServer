using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Services.MusicSearch;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer", Roles = "User,Admin")]
[Route("[controller]")]
public class StreamController : ControllerBase
{
    private readonly IStreamingService _streamingService;
    
    public StreamController(IStreamingService streamingService, IMetadataService metadataService, IConfiguration configuration)
    {
        _streamingService = streamingService;
    }
    
    [HttpGet]
    public async Task<IActionResult> StreamAudio([FromQuery] Guid id)
    {
        var path = await _streamingService.GetStreamingPath(id);
        var mime = "audio/mpeg"; 

        return PhysicalFile(path, mime, enableRangeProcessing: true);
    }
}