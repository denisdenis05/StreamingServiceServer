namespace StreamingServiceServer.Data.Models;

public class Recording
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Length { get; set; }
    public string Cover { get; set; }
    public int PositionInAlbum { get; set; } = 0;
    public Release Release { get; set; }

    public virtual ICollection<RecordingArtistCredit> ArtistCredit { get; set; } = new List<RecordingArtistCredit>();
}