using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public static class LikedSongsPlaylistSelection
{
    public static void Apply(IReadOnlyCollection<Playlist> ownedPlaylists, Guid? playlistId)
    {
        if (playlistId.HasValue && !ownedPlaylists.Any(playlist => playlist.Id == playlistId.Value))
            throw new KeyNotFoundException(LikedSongsPlaylistPolicy.MissingPlaylistMessage);

        foreach (var playlist in ownedPlaylists)
            playlist.IsSystem = playlist.Id == playlistId;
    }
}
