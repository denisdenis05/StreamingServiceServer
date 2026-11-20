using System.Text.Json.Serialization;

namespace StreamingServiceServer.Business.Lyrics;

internal static class LyricsProviderProperties
{
    public const string Identifier = "id";
}

internal sealed record LyricsProviderRecord([property: JsonPropertyName(LyricsProviderProperties.Identifier)] long Identifier, string? TrackName, string? ArtistName,
    string? AlbumName, double Duration, bool Instrumental, string? PlainLyrics, string? SyncedLyrics, string? Lyricsfile)
{
    public bool HasContent => Identifier >= LyricsProtocol.MinimumProviderIdentifier && (Instrumental
        || !string.IsNullOrWhiteSpace(PlainLyrics) || !string.IsNullOrWhiteSpace(SyncedLyrics));
    public LyricsContent Content => new(Identifier, Instrumental ? null : PlainLyrics, Instrumental ? null : SyncedLyrics, Lyricsfile, Instrumental);
}

internal static class LyricsCandidateSelector
{
    private const int NoCandidates = 0;
    private const int SingleValue = 1;
    private const string CarriageReturn = "\r\n";
    private const string LineFeed = "\n";

    public static LyricsContent? Select(LyricsTrack track, LyricsProviderRecord[] records)
    {
        var matching = records.Where(record => record.HasContent
            && LyricsMatchPolicy.MatchesRecording(track, record.TrackName, record.ArtistName, record.Duration)
            && LyricsMatchPolicy.AllowsAlternateAlbum(track.Album, record.AlbumName)).ToArray();
        var matchingAlbum = matching.Where(record => LyricsMatchPolicy.Normalize(record.AlbumName) == LyricsMatchPolicy.Normalize(track.Album)).ToArray();
        var candidates = matchingAlbum.Length > NoCandidates ? matchingAlbum : matching;
        if (candidates.Length == NoCandidates) return null;
        if (candidates.Select(record => record.Instrumental).Distinct().Count() > SingleValue
            || Conflicts(candidates.Select(record => record.PlainLyrics)) || Conflicts(candidates.Select(record => record.SyncedLyrics))
            || Conflicts(candidates.Select(record => record.Lyricsfile)))
            throw new LyricsProviderException(LyricsProtocol.AmbiguousRecording);
        return candidates.OrderByDescending(record => !string.IsNullOrWhiteSpace(record.SyncedLyrics))
            .ThenByDescending(record => !string.IsNullOrWhiteSpace(record.PlainLyrics))
            .ThenByDescending(record => !string.IsNullOrWhiteSpace(record.Lyricsfile))
            .ThenBy(record => Math.Abs(track.DurationSeconds - record.Duration)).ThenBy(record => record.Identifier).First().Content;
    }

    private static bool Conflicts(IEnumerable<string?> values) => values.Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Replace(CarriageReturn, LineFeed).Trim()).Distinct(StringComparer.Ordinal).Count() > SingleValue;
}
