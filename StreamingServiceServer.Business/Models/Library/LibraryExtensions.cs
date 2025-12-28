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

    public static RecordingResponse ToResponse(this PlaylistRecording playlistRecording, bool isLiked = false) => 
        new RecordingResponse
        {
            Id = playlistRecording.RecordingId,
            Title = playlistRecording.Title,
            ArtistName = playlistRecording.Artist,
            ReleaseTitle = playlistRecording.Album,
            Cover = playlistRecording.Cover,
            PositionInAlbum = playlistRecording.Order,
            IsLiked = isLiked
        };
}