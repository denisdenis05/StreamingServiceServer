namespace StreamingServiceServer.Business.Models.Library;

public class PlaylistImportResult
{
    public string OriginalName { get; set; } = string.Empty;
    public string OriginalArtist { get; set; } = string.Empty;
    public string OriginalAlbum { get; set; } = string.Empty;

    public Guid MatchedRecordingId { get; set; } = Guid.Empty;
    public string MatchedTitle { get; set; } = string.Empty;
    public string MatchedArtist { get; set; } = string.Empty;
    public string MatchedAlbum { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;
    public double MatchScore { get; set; }

    /// <summary>True when a match was found in the local DB.</summary>
    public bool IsAvailable { get; set; }
}
