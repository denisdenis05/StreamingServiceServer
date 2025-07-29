namespace StreamingServiceServer.Data.Models;

public class ArtistAlias
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? SortName { get; set; }
    public string? Type { get; set; }
    public string? TypeId { get; set; }
    public string? Locale { get; set; }
    public bool? Primary { get; set; }
    public string? BeginDate { get; set; }
    public string? EndDate { get; set; }

    public Guid ArtistId { get; set; } 
    public virtual Artist Artist { get; set; } = null!;
}