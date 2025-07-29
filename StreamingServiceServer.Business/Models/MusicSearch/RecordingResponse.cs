namespace StreamingServiceServer.Business.Models.MusicSearch;

public class RecordingResponse
{
    public Guid Id { get; set; } 
    public string? Title { get; set; }
    public string? ArtistName { get; set; }
    public string? ReleasseTitle { get; set; }
    public string? Cover {get; set;}
}