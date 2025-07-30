namespace StreamingServiceServer.Data.Models;

public class Release
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public Artist Artist { get; set; }
    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
}