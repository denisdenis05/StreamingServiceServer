using System.Runtime.InteropServices.JavaScript;
using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class MusicBrainzLookupResponse
{
    [JsonPropertyName("packaging")]
    public string Packaging { get; set; }
    
    [JsonPropertyName("media")]
    public ICollection<MediaDto> Media { get; set; } =  new List<MediaDto>();
}