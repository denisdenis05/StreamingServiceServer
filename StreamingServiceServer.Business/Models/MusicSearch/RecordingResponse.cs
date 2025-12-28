namespace StreamingServiceServer.Business.Models.MusicSearch;

public class RecordingResponse
{
    public Guid Id { get; set; } 
    public string? Title { get; set; }
    public string? ArtistName { get; set; }
    public string? ReleaseTitle { get; set; }
    public string? Cover {get; set;}
    public string? SmallCover {get; set;}
    public string? VerySmallCover {get; set;}
    public int PositionInAlbum { get; set; }
    public bool IsLiked { get; set; }
}