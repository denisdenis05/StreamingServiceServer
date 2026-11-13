namespace StreamingServiceServer.Data.Models;

public sealed class RecommendationDailyMix
{
    public Guid UserId { get; set; }
    public DateOnly Day { get; set; }
    public Guid Version { get; set; }
    public Guid[] RecordingIds { get; set; } = [];
    public Guid? SavedPlaylistId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
