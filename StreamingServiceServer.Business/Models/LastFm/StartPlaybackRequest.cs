namespace StreamingServiceServer.Business.Models.LastFm;

public class StartPlaybackRequest
{
    public Guid TrackId { get; set; }
    public string Artist { get; set; }
    public string Track { get; set; }
    public string Album { get; set; }
    public int DurationSeconds { get; set; }
    public bool IsLocalCached { get; set; } = false;
}
