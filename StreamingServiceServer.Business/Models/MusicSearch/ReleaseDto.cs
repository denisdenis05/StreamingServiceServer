using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class ReleaseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("artist-credit")]
    public ICollection<ArtistCreditDto>? ArtistCredit { get; set; } = new  List<ArtistCreditDto>();
    
    public string Cover { get; set; }
    
    public ArtistDto? Artist { get; set; }
    
}