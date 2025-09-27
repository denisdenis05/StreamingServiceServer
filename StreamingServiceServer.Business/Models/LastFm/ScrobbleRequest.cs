namespace StreamingServiceServer.Business.Models.LastFm;

public class ScrobbleRequest
{
    public string Artist { get; set; }
    public string Track { get; set; }
    public string Album { get; set; }
    public long Timestamp { get; set; }
    public Guid UserId { get; set; }
}