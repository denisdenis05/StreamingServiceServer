using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class CoverArt
{
    [JsonPropertyName("image")]
    public string Image { get; set; }
}