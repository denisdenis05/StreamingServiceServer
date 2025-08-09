using System.Runtime.InteropServices.JavaScript;
using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class MusicBrainzLookupResponse
{
    [JsonPropertyName("packaging")]
    public string Packaging { get; set; }
    
    [JsonPropertyName("artist-credit")]
    public ICollection<ArtistCreditDto> ArtistCredit { get; set; } = new List<ArtistCreditDto>();
    
    [JsonPropertyName("title")]
    public string Title { get; set; }
    
    [JsonPropertyName("media")]
    public ICollection<MediaDto> Media { get; set; } =  new List<MediaDto>();
}