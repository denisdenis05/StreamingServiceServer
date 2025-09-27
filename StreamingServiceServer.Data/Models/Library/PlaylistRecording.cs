namespace StreamingServiceServer.Data.Models.Library;

public class PlaylistRecording
{
    public Guid PlaylistId { get; set; }
    public Playlist Playlist { get; set; } = null!;

    public Guid RecordingId { get; set; }
    public Recording Recording { get; set; } = null!;

    public int Order { get; set; } 
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public Guid AddedById { get; set; }
}