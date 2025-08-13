using Microsoft.AspNetCore.Mvc;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.MusicSearch;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Route("[controller]")]
public class MetadataController : ControllerBase
{
    private readonly IMetadataService _metadataService;
    
    public MetadataController(IMetadataService metadataService)
    {
        _metadataService = metadataService;
    }
    
    [HttpGet("search-artist")]
    public async Task<IActionResult> SearchArtist([FromQuery] string query)
    {
        var response = await _metadataService.SearchArtistsAsync(query);
        
        return Ok(response);
    }
    
    
    [HttpGet("search-recording")]
    public async Task<IActionResult> SearchRecording([FromQuery] string query)
    {
        var response = await _metadataService.SearchRecordingsAsync(query);
        
        return Ok(response);
    }
    
    [HttpGet("search-album-recordings")]
    public async Task<IActionResult> SearchAlbumRecordings([FromQuery] string query)
    {
        var response = await _metadataService.SearchAlbumRecordingsAsync(query);
        
        return Ok(response);
    }
    
    [HttpGet("search-albums")]
    public async Task<IActionResult> SearchAlbums([FromQuery] string query)
    {
        var response = await _metadataService.SearchAlbumsAsync(query);
        
        return Ok(response);
    }
    
    [HttpGet("search-album-recordings-by-id")]
    public async Task<IActionResult> SearchAlbumRecordings([FromQuery] Guid albumId)
    {
        var response = await _metadataService.SearchAlbumRecordingsByIdAsync(albumId);
        
        return Ok(response);
    }
    
    [HttpPost("save-artist")]
    public async Task<IActionResult> SaveArtist([FromBody] string query)
    {
        await _metadataService.SearchAndSaveArtistAsync(query);
        
        return Ok();
    }
    
    [HttpPost("save-recording")]
    public async Task<IActionResult> SaveRecording([FromBody] string query)
    {
        await _metadataService.SearchAndSaveRecordingAsync(query);
        
        return Ok();
    }
    
    [HttpPost("save-album")]
    public async Task<IActionResult> SaveAlbum([FromBody] string query)
    {
        await _metadataService.SearchAndSaveAlbumRecordingsAsync(query);
        
        return Ok();
    }
        
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
    
    [HttpPost("queue-to-download-by-query")]
    public async Task<IActionResult> QueueToDownloadByQuery([FromBody] string query)
    {
        await _metadataService.QueueToDownloadByQuery(query);
        
        return Ok();
    }
    
    [HttpPost("queue-to-download-by-id")]
    public async Task<IActionResult> QueueToDownloadById([FromBody] Guid albumId)
    {
        await _metadataService.QueueToDownloadById(albumId);
        
        return Ok();
    }
    
    [HttpPost("bulk-queue-to-download-by-query")]
    public async Task<IActionResult> BulkQueueToDownloadByQuery([FromBody] IEnumerable<AlbumArtistDto> albums)
    {
        await _metadataService.BulkQueueToDownloadByQuery(albums);
        
        return Ok();
    }
}