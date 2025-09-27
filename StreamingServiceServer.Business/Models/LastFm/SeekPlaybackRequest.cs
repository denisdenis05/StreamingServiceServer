namespace StreamingServiceServer.Business.Models.LastFm;

public class SeekPlaybackRequest
{
    public Guid SessionId { get; set; }
    public int FromSeconds { get; set; }
    public int ToSeconds { get; set; }
}