namespace StreamingServiceServer.Data.Models;

public sealed class RecommendationPlaylistCache
{
    public Guid UserId { get; set; }
    public string CacheKey { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}
