using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class RecordingDto
{
    [JsonPropertyName("id")] 
    public Guid Id { get; set; } 

    [JsonPropertyName("score")] 
    public int? Score { get; set; }
    
    [JsonPropertyName("title")] 
    public string? Title { get; set; }
    
    [JsonPropertyName("length")] 
    public int? Length { get; set; }
    
    [JsonPropertyName("disambiguation")] 
    public string? Disambiguation { get; set; }
    
    [JsonPropertyName("artist-credit")] 
    public ICollection<ArtistCreditDto> ArtistCredit { get; set; } = new List<ArtistCreditDto>();
    
    [JsonPropertyName("releases")] 
    public ICollection<ReleaseDto> Releases { get; set; } = new List<ReleaseDto>();
    
    public AlbumCoversDto Cover { get; set; } =  new AlbumCoversDto();
    public int PositionInAlbum { get; set; }
}