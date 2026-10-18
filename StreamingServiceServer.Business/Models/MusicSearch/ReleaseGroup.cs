using System.Text.Json.Serialization;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class ReleaseGroup
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("primary-type")]
    public string? PrimaryType { get; set; }

    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("releases")]
    public ICollection<ReleaseDto> Releases { get; set; } =  new List<ReleaseDto>();

    [JsonPropertyName("artist-credit")]
    public ICollection<ArtistCreditDto> ArtistCredits { get; set; } =  new List<ArtistCreditDto>();
}
