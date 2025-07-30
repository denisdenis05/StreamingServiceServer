namespace StreamingServiceServer.Data.Models;

public class Artist
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SortName { get; set; }
    public string? Type { get; set; }
    public string? TypeId { get; set; }
    public string? Gender { get; set; }
    public string? GenderId { get; set; }
    public string? Country { get; set; }
    public virtual ICollection<ArtistAlias> Aliases { get; set; } = new List<ArtistAlias>();
    public virtual ICollection<ArtistTag> Tags { get; set; } = new List<ArtistTag>();
    public virtual ICollection<Release> Releases { get; set; } = new List<Release>();
}