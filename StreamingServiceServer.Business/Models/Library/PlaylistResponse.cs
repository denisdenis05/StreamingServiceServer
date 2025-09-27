namespace StreamingServiceServer.Business.Models.Library;

public class PlaylistResponse
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Owner { get; set; }
    public string Cover { get; set; }
    public int RecordingCount { get; set; }
    public DateTime CreatedAt { get; set; }
}
