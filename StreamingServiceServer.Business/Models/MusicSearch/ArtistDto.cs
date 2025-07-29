using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Models.MusicSearch;

public class ArtistDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("type-id")]
    public string? TypeId { get; set; }

    [JsonPropertyName("score")]
    public int? Score { get; set; }

    [JsonPropertyName("gender-id")]
    public string? GenderId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sort-name")]
    public string? SortName { get; set; }

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonPropertyName("aliases")]
    public List<AliasDto>? Aliases { get; set; }

    [JsonPropertyName("tags")]
    public List<TagDto>? Tags { get; set; }
}