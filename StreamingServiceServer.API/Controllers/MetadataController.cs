using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.MusicSearch;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer", Roles = "User,Admin")]
[Route("[controller]")]
public class MetadataController : ControllerBase
{
    private readonly IMetadataService _metadataService;
    
    public MetadataController(IMetadataService metadataService)
    {
        _metadataService = metadataService;
    }
    
    [HttpGet("search")]
    public async Task<IActionResult> GeneralSearch([FromQuery] string query, [FromQuery] int limit = 10)
    {
        var response = await _metadataService.GeneralSearch(query, limit);
        
        return Ok(response);
    }
    
    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    [HttpPost("save-artist")]
    public async Task<IActionResult> SaveArtist([FromBody] string query)
    {
        await _metadataService.SearchAndSaveArtistAsync(query);
        
        return Ok();
    }
    
    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    [HttpPost("save-recording")]
    public async Task<IActionResult> SaveRecording([FromBody] string query)
    {
        await _metadataService.SearchAndSaveRecordingAsync(query);
        
        return Ok();
    }
    
    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    [HttpPost("save-album")]
    public async Task<IActionResult> SaveAlbum([FromBody] string query)
    {
        await _metadataService.SearchAndSaveAlbumRecordingsAsync(query);
        
        return Ok();
    }
        
    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    [HttpPost("save-album-by-id")]
    public async Task<IActionResult> SaveAlbumById([FromBody] Guid albumId)
    {
        await _metadataService.SearchAndSaveAlbumRecordingsByIdAsync(albumId);
        
        return Ok();
    }
    
    [HttpGet("get-available-artists")]
    public async Task<IActionResult> GetAllArtists()
    {
        var response = await _metadataService.GetAllArtistNames();
        
        return Ok(response);
    }
    
    [HttpGet("get-available-recordings")]
    public async Task<IActionResult> GetAllRecordings()
    {
        var response = await _metadataService.GetAllRecordings();
        
        return Ok(response);
    }
    
    [HttpGet("get-recordings")]
    public async Task<IActionResult> GetAllRecordings([FromQuery] Guid albumId)
    {
        var response = await _metadataService.GetRecordingsByAlbumId(albumId);
        
        return Ok(response);
    }
    
    [HttpGet("get-available-albums")]
    public async Task<IActionResult> GetAllAlbums()
    {
        var response = await _metadataService.GetAllAlbums();
        
        return Ok(response);
    }
    
    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    [HttpPost("queue-to-download-by-query")]
    public async Task<IActionResult> QueueToDownloadByQuery([FromBody] AlbumArtistDto query)
    {
        await _metadataService.QueueToDownloadByQuery(query.Album,  query.Artist);
        
        return Ok();
    }
    
    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    [HttpPost("queue-to-download-by-id")]
    public async Task<IActionResult> QueueToDownloadById([FromBody] Guid albumId)
    {
        await _metadataService.QueueToDownloadById(albumId);
        
        return Ok();
    }
    
    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    [HttpPost("bulk-queue-to-download-by-query")]
    public async Task<IActionResult> BulkQueueToDownloadByQuery([FromBody] IEnumerable<AlbumArtistDto> albums)
    {
        await _metadataService.BulkQueueToDownloadByQuery(albums);
        
        return Ok();
    }

    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    [HttpPost("refresh-all-album-covers")] public async Task<IActionResult> RefreshAllAlbumCovers()
    {
        await _metadataService.RefreshAllAlbumCovers();
        
        return Ok();
    }
}