namespace StreamingServiceServer.Business.Models.MusicSearch;

public class DbSearchResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasMoreTracks { get; set; }

    // Tier 1 — exact substring matches, paginated
    public List<RecordingResponse> Tracks { get; set; } = new();
    public List<ReleaseResponse> Albums { get; set; } = new();
    public List<ArtistResponse> Artists { get; set; } = new();

    // Tier 2 — fuzzy/trigram matches not already in Tier 1 (page 1 only)
    public List<RecordingResponse> FuzzyTracks { get; set; } = new();
    public List<ReleaseResponse> FuzzyAlbums { get; set; } = new();
    public List<ArtistResponse> FuzzyArtists { get; set; } = new();
}
