namespace StreamingServiceServer.Data.Models.Library;

public class PlaylistRecording
{
    public Guid PlaylistId { get; set; }
    public Playlist Playlist { get; set; } = null!;

    public Guid RecordingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;

    public int Order { get; set; } 
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public Guid AddedById { get; set; }
}