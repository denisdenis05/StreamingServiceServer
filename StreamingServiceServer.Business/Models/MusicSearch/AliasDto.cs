using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class AliasDto
{
    [JsonPropertyName("sort-name")]
    public string? SortName { get; set; }

    [JsonPropertyName("type-id")]
    public string? TypeId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("locale")]
    public string? Locale { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("primary")]
    public bool? Primary { get; set; }

    [JsonPropertyName("begin-date")]
    public string? BeginDate { get; set; }

    [JsonPropertyName("end-date")]
    public string? EndDate { get; set; }
}