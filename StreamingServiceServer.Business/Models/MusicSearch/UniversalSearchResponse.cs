using System.Text.Json.Serialization;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class UniversalSearchResponse
{
    public BestMatch? BestMatch { get; set; }
    public List<ArtistResponse> Artists { get; set; } = new();
    public List<RecordingResponse> Tracks { get; set; } = new();
    public List<ReleaseResponse> Albums { get; set; } = new();
    public List<Playlist> Playlists { get; set; } = new();
}
