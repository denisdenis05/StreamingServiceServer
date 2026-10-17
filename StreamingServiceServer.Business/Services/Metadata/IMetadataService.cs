using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public interface IMetadataService
{
    Task<RecordingResponse> SearchRecordingByIdAsync(Guid id);

    Task<UniversalSearchResponse> GeneralSearch(string query, int limit, Guid userId, CancellationToken cancellationToken = default);
    Task<DbSearchResponse> DbSearch(string query, int page, int pageSize, Guid userId, CancellationToken cancellationToken = default);

    Task SearchAndSaveRecordingAsync(string query);
    Task SearchAndSaveArtistAsync(string query);
    Task SearchAndSaveAlbumRecordingsAsync(string query);
    Task SearchAndSaveAlbumRecordingsByIdAsync(Guid albumId);

    Task<List<string>> GetAllArtistNames();
    Task<List<RecordingResponse>> GetAllRecordings();
    Task<List<RecordingResponse>> GetSuggestedRecordings(int count);
    Task<List<ReleaseResponse>> GetAllAlbums();
    Task<RecordingResponse> GetRecordingById(Guid id, Guid userId);
    Task<ICollection<RecordingResponse>> GetRecordingsByAlbumId(Guid id, Guid userId);

    Task BulkQueueToDownloadByQuery(IEnumerable<AlbumArtistDto> albums);
    Task QueueToDownloadByQuery(string albumQuery, string? artistQuery = null);
    Task QueueToDownloadById(Guid id);

    Task<CoverMetadataRefresh?> GetCoverMetadataRefresh(CancellationToken cancellationToken = default);
    Task CancelCoverMetadataRefresh(CancellationToken cancellationToken = default);
    Task RefreshAllAlbumCovers(CancellationToken cancellationToken = default);
}
