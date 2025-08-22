namespace StreamingServiceServer.Data.Models;

public class Release
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public string Cover { get; set; }
    public string? SmallCover { get; set; }
    public string? VerySmallCover { get; set; }
    public Artist Artist { get; set; }
    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
}