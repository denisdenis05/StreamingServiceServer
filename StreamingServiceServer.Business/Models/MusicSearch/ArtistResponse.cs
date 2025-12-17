namespace StreamingServiceServer.Business.Models.MusicSearch;

public class ArtistResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Image { get; set; }
}
