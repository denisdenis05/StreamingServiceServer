namespace StreamingServiceServer.Business.Models.MusicSearch;

public class ReleaseResponse
{
    public Guid Id { get; set; } 
    public string? Title { get; set; }
    public string? ArtistName { get; set; }
    public string? Cover {get; set;}
}