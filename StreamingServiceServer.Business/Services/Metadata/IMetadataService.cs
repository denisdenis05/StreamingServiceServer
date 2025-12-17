using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public interface IMetadataService
{
    Task<RecordingResponse> SearchRecordingByIdAsync(Guid id);
    
    Task<UniversalSearchResponse> GeneralSearch(string query, int limit);
    
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