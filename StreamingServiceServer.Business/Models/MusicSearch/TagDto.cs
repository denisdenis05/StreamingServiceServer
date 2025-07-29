using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class TagDto
{
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}