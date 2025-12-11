namespace StreamingServiceServer.Business.Models.Library;

public class CreatePlaylistRequest
{
    public string? Name { get; set; }
    public List<Guid> RecordingIds { get; set; } = new();
}
