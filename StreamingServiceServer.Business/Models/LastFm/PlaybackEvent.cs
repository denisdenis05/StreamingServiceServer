namespace StreamingServiceServer.Business.Models.LastFm;

public class PlaybackEvent
{
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } 
    public int? SeekFromSeconds { get; set; } 
    public int? SeekToSeconds { get; set; } 
}