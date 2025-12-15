using System.Text.Json.Serialization;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class UniversalSearchResponse
{
    public BestMatch? BestMatch { get; set; }
    public List<ArtistDto> Artists { get; set; } = new();
    public List<RecordingDto> Tracks { get; set; } = new();
    public List<ReleaseDto> Albums { get; set; } = new();
    public List<Playlist> Playlists { get; set; } = new();
}
