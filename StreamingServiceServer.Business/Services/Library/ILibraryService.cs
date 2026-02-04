using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public interface ILibraryService
{
    Task<Playlist> CreatePlaylistAsync(Guid ownerId, CreatePlaylistRequest request, CancellationToken cancellationToken);
    Task<List<RecordingResponse>> GetPlaylistRecordingsAsync(Guid playlistId, Guid userId, int page, int pageSize, CancellationToken cancellationToken);
    Task<List<PlaylistResponse>> GetUserPlaylistsAsync(Guid userId, CancellationToken cancellationToken);
    Task<List<ReleaseResponse>> GetLibraryAsync(Guid userId, CancellationToken cancellationToken);
    Task RemoveRecordingFromPlaylistAsync(Guid userId, Guid playlistId, Guid recordingId, CancellationToken cancellationToken);
    Task UpdatePlaylistAsync(Guid userId, Guid playlistId, string name, CancellationToken cancellationToken);
    Task AddRecordingsToPlaylistAsync(Guid userId, Guid playlistId, List<AddSongToPlaylistRequest> songs, CancellationToken cancellationToken);
    Task ToggleLikeAsync(Guid userId, Guid recordingId, AddSongToPlaylistRequest songDetails, CancellationToken cancellationToken);
    Task<HashSet<Guid>> GetLikedRecordingIdsAsync(Guid userId, CancellationToken cancellationToken);
    Task AddRecordingsToPlaylistFuzzyAsync(Guid userId, Guid playlistId, List<AddRecordingToPlaylistFuzzyRequest> requests, CancellationToken cancellationToken);
}