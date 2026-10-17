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
    public async Task<IActionResult> StreamAudio([FromQuery] Guid id, CancellationToken cancellationToken = default)
    {
        string trackPath;
        try 
        {
            trackPath = await _streamingService.GetStreamingPath(id, cancellationToken);
        }
        catch (RecordingAudioUnavailableException)
        {
            var recording = await _metadataService.SearchRecordingByIdAsync(id).WaitAsync(cancellationToken);
            if (recording == null)
            {
                return NotFound("Recording not found");
            }

            try
            {
                trackPath = await _previewDownloaderService.DownloadPreview(recording, cancellationToken);
            }
            catch (StreamingServiceServer.PreviewDownloader.PreviewCapacityException)
            {
                Response.Headers["Retry-After"] = "10";
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Preview capacity is exhausted. Retry later.");
            }
        } 

        var encodedSegments = trackPath
            .Split('/')
            .Select(Uri.EscapeDataString);

        var encodedPath = string.Join("/", encodedSegments);

        var internalPath = $"/protected/{encodedPath}";

        Response.Headers["X-Accel-Redirect"] = internalPath;
        Response.Headers["Accept-Ranges"] = "bytes";
        Response.Headers["Content-Type"] = _streamingService.GetContentType(trackPath);
        return new EmptyResult();

    }
}
