namespace StreamingServiceServer.Business.Models.MusicSearch;

public class RecordingResponse
{
    public Guid Id { get; set; } 
    public Guid? ReleaseId { get; set; }
    public string? Title { get; set; }
    public string? ArtistName { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ArtistAliases { get; set; }
    public string? ReleaseTitle { get; set; }
    public string? Cover {get; set;}
    public string? SmallCover {get; set;}
    public string? VerySmallCover {get; set;}
    public int PositionInAlbum { get; set; }
    public bool IsLiked { get; set; }
    
    public Guid? SuggestedRecordingId { get; set; }
    public string? SuggestedTitle { get; set; }
    public string? SuggestedArtist { get; set; }
    public string? SuggestedAlbum { get; set; }
}
