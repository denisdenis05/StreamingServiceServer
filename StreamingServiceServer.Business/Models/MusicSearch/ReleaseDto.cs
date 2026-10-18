using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class ReleaseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonPropertyName("track-count")]
    public int? TrackCount { get; set; }

    [JsonPropertyName("artist-credit")]
    public ICollection<ArtistCreditDto>? ArtistCredit { get; set; } = new  List<ArtistCreditDto>();

    [JsonPropertyName("release-group")]
    public ReleaseGroup? ReleaseGroup { get; set; } 

    [JsonPropertyName("media")]
    public ICollection<MediaDto>? Media { get; set; } = new List<MediaDto>();
    public AlbumCoversDto Cover { get; set; } =  new AlbumCoversDto();

    public ArtistDto? Artist { get; set; }

    [JsonPropertyName("score")]
    public int Score { get; set; }
}
