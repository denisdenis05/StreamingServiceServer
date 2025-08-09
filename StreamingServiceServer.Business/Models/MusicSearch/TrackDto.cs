using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class TrackDto
{
    [JsonPropertyName("position")] 
    public int PositionInAlbum { get; set; } = 0;
    
    [JsonPropertyName("recording")]
    public RecordingDto Recording { get; set; }
}