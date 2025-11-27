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
    private readonly StreamingServiceServer.PreviewDownloader.IPreviewDownloaderService _previewDownloaderService;
    private readonly IMetadataService _metadataService;
    
    public StreamController(IStreamingService streamingService, ILastFmService lastFmService, StreamingServiceServer.PreviewDownloader.IPreviewDownloaderService previewDownloaderService, IMetadataService metadataService)
    {
        _streamingService = streamingService;
        _lastFmService = lastFmService;
        _previewDownloaderService = previewDownloaderService;
        _metadataService = metadataService;
    }
    
    [HttpGet]
    public async Task<IActionResult> StreamAudio([FromQuery] Guid id)
    {
        string trackPath;
        try 
        {
            trackPath = await _streamingService.GetStreamingPath(id);
        }
        catch (Exception)
        {
            var recording = await _metadataService.SearchRecordingByIdAsync(id);
            if (recording == null)
            {
                return NotFound("Recording not found");
            }

            trackPath = await _previewDownloaderService.DownloadPreview(recording);
        } 
        
        var encodedSegments = trackPath
            .Split('/')
            .Select(Uri.EscapeDataString);

        var encodedPath = string.Join("/", encodedSegments);
        
        var internalPath = $"/protected/{encodedPath}";

        Response.Headers["X-Accel-Redirect"] = internalPath;
        return new EmptyResult();

    }
}