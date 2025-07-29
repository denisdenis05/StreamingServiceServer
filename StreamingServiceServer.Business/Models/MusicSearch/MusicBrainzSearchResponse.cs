using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class MusicBrainzSearchResponse
{
    [JsonPropertyName("created")]
    public DateTime Created { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }
    
    [JsonPropertyName("artists")]
    public List<ArtistDto>? Artists { get; set; }

    [JsonPropertyName("recordings")]
    public List<RecordingDto>? Recordings { get; set; }
    
    
    [JsonPropertyName("release-groups")]
    public List<ReleaseGroup>? ReleaseGroups { get; set; }
}