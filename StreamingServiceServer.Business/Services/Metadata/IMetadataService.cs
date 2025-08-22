using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public interface IMetadataService
{
    Task<List<Artist>> SearchArtistsAsync(string query);
    Task<List<RecordingResponse>> SearchRecordingsAsync(string query);
    Task<List<ReleaseResponse>> SearchAlbumsAsync(string query);
    Task<List<RecordingResponse>> SearchAlbumRecordingsAsync(string query);
    Task<List<RecordingResponse>> SearchAlbumRecordingsByIdAsync(Guid albumId);
    
    Task SearchAndSaveRecordingAsync(string query);
    Task SearchAndSaveArtistAsync(string query);
    Task SearchAndSaveAlbumRecordingsAsync(string query);
    Task SearchAndSaveAlbumRecordingsByIdAsync(Guid albumId);
    
    Task<List<string>> GetAllArtistNames();
    Task<List<RecordingResponse>> GetAllRecordings();
    Task<List<ReleaseResponse>> GetAllAlbums();
    Task<RecordingResponse> GetRecordingById(Guid id);
    Task<ICollection<RecordingResponse>> GetRecordingsByAlbumId(Guid id);

    Task BulkQueueToDownloadByQuery(IEnumerable<AlbumArtistDto> albums);
    Task QueueToDownloadByQuery(string albumQuery, string? artistQuery = null);
    Task QueueToDownloadById(Guid id);
    
    Task RefreshAllAlbumCovers();
}