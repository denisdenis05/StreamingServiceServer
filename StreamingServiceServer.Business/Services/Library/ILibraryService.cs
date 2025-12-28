using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public interface ILibraryService
{
    Task<Playlist> CreatePlaylistAsync(Guid ownerId, CreatePlaylistRequest request, CancellationToken cancellationToken);
    Task<List<RecordingResponse>> GetPlaylistRecordingsAsync(Guid playlistId, CancellationToken cancellationToken);
    Task<List<PlaylistResponse>> GetUserPlaylistsAsync(Guid userId, CancellationToken cancellationToken);
    Task<List<ReleaseResponse>> GetLibraryAsync(Guid userId, CancellationToken cancellationToken);
    Task RemoveRecordingFromPlaylistAsync(Guid userId, Guid playlistId, Guid recordingId, CancellationToken cancellationToken);
    Task UpdatePlaylistAsync(Guid userId, Guid playlistId, string name, CancellationToken cancellationToken);
    Task AddRecordingsToPlaylistAsync(Guid userId, Guid playlistId, List<AddSongToPlaylistRequest> songs, CancellationToken cancellationToken);
}