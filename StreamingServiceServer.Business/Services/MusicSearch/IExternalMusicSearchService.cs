using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public interface IExternalMusicSearchService
{
    Task<List<ArtistDto>> SearchArtistsAsync(string query);
    Task<List<RecordingDto>> SearchRecordingsAsync(string query);
    Task<List<RecordingDto>> SearchAlbumRecordingsAsync(string query);
    Task<List<ReleaseDto>> SearchAlbumsAsync(string query);
    Task<List<RecordingDto>> SearchAlbumRecordingsByIdAsync(Guid albumId);
    Task<ReleaseResponse> GetAlbumDetails(Guid albumId);
}