namespace StreamingServiceServer.Business.Models.Library;

public class AddRecordingToPlaylistFuzzyRequest
{
    public DateTimeOffset? AddedAt { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string ReleaseName { get; set; } = string.Empty;
    public Guid? ConfirmedRecordingId { get; set; }
    public bool AcceptSuggestion { get; set; }
}
