namespace StreamingServiceServer.Business.Models.LastFm;

public class StopPlaybackRequest
{
    public Guid SessionId { get; set; }
    public int PlayedSeconds { get; set; }
}