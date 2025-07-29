namespace StreamingServiceServer.Data.Models;

public class ArtistTag
{
    public Guid Id { get; set; }
    public string? Name { get; set; }

    public Guid ArtistId { get; set; } 
    public virtual Artist Artist { get; set; } = null!;
}