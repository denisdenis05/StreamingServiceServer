using System.Runtime.InteropServices.JavaScript;
using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class CoverArtResponse
{
    [JsonPropertyName("images")]
    public ICollection<CoverArt> Images { get; set; } = new List<CoverArt>();
}