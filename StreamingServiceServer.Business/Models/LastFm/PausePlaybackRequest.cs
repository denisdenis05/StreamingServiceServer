namespace StreamingServiceServer.Business.Models.LastFm;

public class PausePlaybackRequest
{
    public Guid SessionId { get; set; }
    public int DeltaListenedSeconds { get; set; }
    public int TotalListenedSeconds { get; set; }
}