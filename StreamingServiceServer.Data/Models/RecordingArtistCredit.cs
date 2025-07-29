namespace StreamingServiceServer.Data.Models;

public class RecordingArtistCredit
{
    public Guid Id { get; set; }
    public string? Name { get; set; }

    public Guid RecordingId { get; set; }
    public Guid? ArtistId { get; set; }

    public virtual Artist? Artist { get; set; }
}