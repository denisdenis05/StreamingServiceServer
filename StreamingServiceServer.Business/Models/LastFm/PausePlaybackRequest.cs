namespace StreamingServiceServer.Business.Models.LastFm;

public class PausePlaybackRequest
{
    public Guid SessionId { get; set; }
    public int PlayedSeconds { get; set; }
}