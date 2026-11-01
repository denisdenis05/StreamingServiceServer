using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Business.Models.LastFm;
using StreamingServiceServer.Data;

namespace StreamingServiceServer.Business.Services.LastFm;

public sealed record PlaybackCollectionContext(Guid? SourceAlbumId, Guid? PlaylistId)
{
    public static readonly PlaybackCollectionContext Unknown = new(null, null);

    public static async Task<PlaybackCollectionContext> ResolveAsync(
        StreamingDbContext database, Guid accountIdentifier, StartPlaybackRequest request)
    {
        if (request.TrackId == Guid.Empty ||
            request.SourceAlbumId == Guid.Empty || request.PlaylistId == Guid.Empty ||
            request.SourceAlbumId.HasValue && request.PlaylistId.HasValue)
        {
            return Unknown;
        }

        if (request.SourceAlbumId is Guid albumIdentifier)
        {
            var recordingBelongsToAlbum = await database.Recordings.AnyAsync(recording =>
                recording.Id == request.TrackId && recording.Release.Id == albumIdentifier);
            return recordingBelongsToAlbum ? new(albumIdentifier, null) : Unknown;
        }

        if (request.PlaylistId is Guid playlistIdentifier)
        {
            var recordingBelongsToOwnedPlaylist = await database.PlaylistRecordings.AnyAsync(playlistRecording =>
                playlistRecording.PlaylistId == playlistIdentifier &&
                playlistRecording.RecordingId == request.TrackId &&
                playlistRecording.Playlist.OwnerId == accountIdentifier);
            return recordingBelongsToOwnedPlaylist ? new(null, playlistIdentifier) : Unknown;
        }

        return Unknown;
    }
}
