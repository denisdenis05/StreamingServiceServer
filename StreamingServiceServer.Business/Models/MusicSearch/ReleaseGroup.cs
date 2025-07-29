using System.Text.Json.Serialization;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class ReleaseGroup
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }
    
    [JsonPropertyName("releases")]
    public ICollection<ReleaseDto> Releases { get; set; } =  new List<ReleaseDto>();
}