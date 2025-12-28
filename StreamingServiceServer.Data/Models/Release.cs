namespace StreamingServiceServer.Data.Models;

public class Release
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public string Cover { get; set; }
    public string? SmallCover { get; set; }
    public string? VerySmallCover { get; set; }
    public string OptimizedCover { get; set; } = string.Empty;
    public string OptimizedSmallCover { get; set; } = string.Empty;
    public string OptimizedVerySmallCover { get; set; } = string.Empty;
    public Artist Artist { get; set; }
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
}