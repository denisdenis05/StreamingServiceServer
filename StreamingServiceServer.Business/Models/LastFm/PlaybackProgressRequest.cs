using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Models.LastFm;

public class PlaybackProgressRequest
{
    public Guid SessionId { get; set; }
    public int PlayedSeconds { get; set; }
    public List<PlaybackEvent>? Events { get; set; }
}