namespace StreamingServiceServer.Business.Models.LastFm;

public class PlaybackEvent
{
    public string EventType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public int? SeekFromSeconds { get; set; }
    public int? SeekToSeconds { get; set; }
    public int? Position { get; set; } 
}