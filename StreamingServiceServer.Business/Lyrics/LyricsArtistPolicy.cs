using System.Text.Json;

namespace StreamingServiceServer.Business.Lyrics;

public static class LyricsArtistPolicy
{
    private const string ArtistSeparator = " & ";
    private const int NoArtists = 0;
    private const int SingleArtist = 1;
    private const string UnknownArtist = "UNKNOWN ARTIST";
    private const string Unknown = "UNKNOWN";
    private const string VariousArtists = "VARIOUS ARTISTS";

    public static string[] Credits(IEnumerable<string?> credits) => credits.Where(credit => !string.IsNullOrWhiteSpace(credit))
        .Select(credit => credit!.Trim()).GroupBy(LyricsMatchPolicy.Normalize, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => group.OrderBy(credit => credit, StringComparer.Ordinal).First()).ToArray();

    public static string Primary(string? albumArtist, string[] credits)
    {
        if (credits.Length == NoArtists) return albumArtist?.Trim() ?? string.Empty;
        var matchingAlbumArtist = credits.FirstOrDefault(credit => LyricsMatchPolicy.Normalize(credit) == LyricsMatchPolicy.Normalize(albumArtist));
        if (matchingAlbumArtist is not null) return albumArtist!.Trim();
        return string.Join(ArtistSeparator, credits);
    }

    public static string? Revision(string[] credits) => credits.Length > SingleArtist
        ? JsonSerializer.Serialize(credits.Select(LyricsMatchPolicy.Normalize)) : null;

    public static bool IsUnknown(string artist) => LyricsMatchPolicy.Normalize(artist) is UnknownArtist or Unknown or VariousArtists;
}
