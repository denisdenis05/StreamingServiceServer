using System.Text;
using System.Text.RegularExpressions;

namespace StreamingServiceServer.Business.Lyrics;

public static class LyricsMatchPolicy
{
    private const string WhitespacePattern = @"\s+";
    private const string SingleSpace = " ";
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    public static string Normalize(string? value) => Regex.Replace((value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim(),
        WhitespacePattern, SingleSpace, RegexOptions.None, PatternTimeout).ToUpperInvariant();

    public static string? Rejection(LyricsTrack track)
    {
        if (track.HasNamingIssue) return LyricsProtocol.NamingIssue;
        if (!UsableName(track.Title) || !UsableName(track.Artist) || !UsableName(track.Album)
            || track.DurationSeconds < LyricsProtocol.MinimumDurationSeconds || track.DurationSeconds > LyricsProtocol.MaximumDurationSeconds)
            return LyricsProtocol.InvalidMetadata;
        return null;
    }

    public static bool Matches(LyricsTrack track, string? title, string? artist, string? album, double durationSeconds) =>
        Normalize(track.Title) == Normalize(title) && Normalize(track.Artist) == Normalize(artist)
        && Normalize(track.Album) == Normalize(album) && double.IsFinite(durationSeconds)
        && Math.Abs(track.DurationSeconds - durationSeconds) <= LyricsProtocol.DurationToleranceSeconds;

    private static bool UsableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > LyricsProtocol.MaximumNameLength || name.Any(char.IsControl)) return false;
        return true;
    }
}
