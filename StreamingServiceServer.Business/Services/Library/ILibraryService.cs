using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public interface ILibraryService
{
    Task<Playlist> CreatePlaylistAsync(Guid ownerId, RecordingIdList request, CancellationToken cancellationToken);
    Task<List<RecordingResponse>> GetPlaylistRecordingsAsync(Guid playlistId, CancellationToken cancellationToken);
    Task<List<PlaylistResponse>> GetUserPlaylistsAsync(Guid userId, CancellationToken cancellationToken);
}