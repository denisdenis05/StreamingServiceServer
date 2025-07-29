using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class MediaDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("track-count")]
    public int? TrackCount { get; set; }
    
    [JsonPropertyName("tracks")]
    public ICollection<TrackDto> Tracks { get; set; } = new List<TrackDto>();
}