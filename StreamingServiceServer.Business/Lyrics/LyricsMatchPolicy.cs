using System.Text;
using System.Text.RegularExpressions;

namespace StreamingServiceServer.Business.Lyrics;

public static class LyricsMatchPolicy
{
    private const string WhitespacePattern = @"\s+";
    private const string SingleSpace = " ";
    private const char StraightApostrophe = '\'';
    private const char RightApostrophe = '\u2019';
    private const char LeftApostrophe = '\u2018';
    private const string PerformanceVersionPattern = @"\b(LIVE|REMIX|REMIXED|ACOUSTIC|INSTRUMENTAL|KARAOKE|DEMO|RADIO EDIT|EXTENDED|RE-RECORDED|EXPLICIT|CLEAN|CENSORED)\b";
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    public static string Normalize(string? value) => Regex.Replace((value ?? string.Empty).Normalize(NormalizationForm.FormKC)
            .Replace(RightApostrophe, StraightApostrophe).Replace(LeftApostrophe, StraightApostrophe).Trim(),
        WhitespacePattern, SingleSpace, RegexOptions.None, PatternTimeout).ToUpperInvariant();

    public static string SearchName(string value) => value.Normalize(NormalizationForm.FormKC)
        .Replace(RightApostrophe, StraightApostrophe).Replace(LeftApostrophe, StraightApostrophe).Trim();

    public static string? Rejection(LyricsTrack track)
    {
        if (track.HasNamingIssue) return LyricsProtocol.NamingIssue;
        if (!UsableName(track.Title) || !UsableName(track.Artist) || LyricsArtistPolicy.IsUnknown(track.Artist) || !UsableName(track.Album)
            || track.DurationSeconds < LyricsProtocol.MinimumDurationSeconds || track.DurationSeconds > LyricsProtocol.MaximumDurationSeconds)
            return LyricsProtocol.InvalidMetadata;
        return null;
    }

    public static bool Matches(LyricsTrack track, string? title, string? artist, string? album, double durationSeconds) =>
        MatchesRecording(track, title, artist, durationSeconds) && Normalize(track.Album) == Normalize(album);

    public static bool MatchesRecording(LyricsTrack track, string? title, string? artist, double durationSeconds) =>
        Normalize(track.Title) == Normalize(title) && Normalize(track.Artist) == Normalize(artist) && double.IsFinite(durationSeconds)
        && Math.Abs(track.DurationSeconds - durationSeconds) <= LyricsProtocol.DurationToleranceSeconds;

    public static bool AllowsAlternateAlbum(string album, string? candidateAlbum)
    {
        var normalizedAlbum = Normalize(album);
        var normalizedCandidate = Normalize(candidateAlbum);
        return normalizedAlbum == normalizedCandidate || (!Regex.IsMatch(normalizedAlbum, PerformanceVersionPattern, RegexOptions.None, PatternTimeout)
            && !Regex.IsMatch(normalizedCandidate, PerformanceVersionPattern, RegexOptions.None, PatternTimeout));
    }

    private static bool UsableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > LyricsProtocol.MaximumNameLength || name.Any(char.IsControl)) return false;
        return true;
    }
}
