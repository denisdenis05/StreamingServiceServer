using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FuzzySharp;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceDownloader.Helpers;

public static class AudioMatchPolicy
{
    public static string Normalize(string? value)
    {
        var text = StringSanitizers.NormalizeSpecialCharacters(value ?? string.Empty).Normalize(NormalizationForm.FormD);
        text = new string(text.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " "), @"\s+", " ").Trim();
    }

    public static bool ContainsName(string value, string name)
    {
        var normalized = Normalize(name);
        return normalized.Length > 0 && (" " + Normalize(value) + " ").Contains(" " + normalized + " ", StringComparison.Ordinal);
    }

    public static bool SameTitle(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual)) return false;
        var target = Normalize(WithoutFeaturedCredit(expected));
        var candidate = Normalize(WithoutFeaturedCredit(actual));
        if (target.Length == 0 || candidate.Length == 0) return expected.Trim().Equals(actual.Trim(), StringComparison.OrdinalIgnoreCase);
        return target == candidate || (target.Length >= 10 && candidate.Length >= 10 && Fuzz.Ratio(target, candidate) >= 92);
    }

    public static bool MatchesAlbumPath(string artist, string album, string directory)
    {
        var core = Regex.Replace(album, @"\([^)]*\)|\[[^]]*\]", " ").Trim();
        return ContainsName(directory, artist) && (ContainsName(directory, album) || ContainsName(directory, core));
    }

    public static int TrackScore(RecordingResponse recording, string filename, AudioMetadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.Title))
        {
            if (!string.IsNullOrWhiteSpace(metadata.Artist) && !string.IsNullOrWhiteSpace(recording.ArtistName)
                && !recording.ArtistName.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
                && !ArtistMatches(recording.ArtistName, metadata.Artist)
                && !(recording.ArtistAliases ?? []).Any(alias => ArtistMatches(alias, metadata.Artist))) return 0;
            if (!string.IsNullOrWhiteSpace(metadata.Album) && !string.IsNullOrWhiteSpace(recording.ReleaseTitle)
                && !SameTitle(recording.ReleaseTitle, metadata.Album)) return 0;
            return SameTitle(recording.Title, metadata.Title) ? 100 : 0;
        }
        var name = Path.GetFileNameWithoutExtension(filename);
        name = Regex.Replace(name, @"^\s*\d+(?:[-.]\d+)?\s*[-._ ]*", string.Empty);
        var artistPrefix = recording.ArtistName + " - ";
        if (name.StartsWith(artistPrefix, StringComparison.OrdinalIgnoreCase)) name = name[artistPrefix.Length..];
        return SameTitle(recording.Title, name) ? 90 : 0;
    }

    private static bool ArtistMatches(string expected, string actual) =>
        Regex.Split(actual, @";|\s+(?:feat\.?|ft\.?|featuring)\s+", RegexOptions.IgnoreCase)
            .Any(credit => SameTitle(expected, credit));

    private static string WithoutFeaturedCredit(string value) => Regex.Replace(value,
        @"\s*[\[(]\s*(?:feat\.?|ft\.?|featuring)\s+[^\])]+[\])]|\s+(?:feat\.?|ft\.?|featuring)\s+.+$",
        string.Empty, RegexOptions.IgnoreCase).Trim();
}
