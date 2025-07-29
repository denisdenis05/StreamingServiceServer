using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class TrackDto
{
    [JsonPropertyName("recording")]
    public RecordingDto Recording { get; set; }
}