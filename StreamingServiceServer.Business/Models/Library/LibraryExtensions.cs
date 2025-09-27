using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Models.Library;

public static class LibraryExtensions
{
    public static PlaylistResponse ToResponse(this Playlist playlist) =>
        new PlaylistResponse
        {
            Id = playlist.Id,
            Name = playlist.Name,
            Owner = playlist.Owner.Username,
            RecordingCount = playlist.PlaylistRecordings.Count,
            Cover = playlist.Cover,
            CreatedAt = playlist.CreatedAt 
        };

    public static RecordingResponse ToResponse(this PlaylistRecording playlistRecording) => 
        new RecordingResponse
        {
            Id = playlistRecording.Recording.Id,
            Title = playlistRecording.Recording.Title,
            ArtistName = playlistRecording.Recording.ArtistCredit
                .Select(ac => ac.Artist.Name)
                .FirstOrDefault(),
            ReleaseTitle = playlistRecording.Recording.Release.Title,
            Cover = playlistRecording.Recording.Cover,
            PositionInAlbum = playlistRecording.Order
        };
}