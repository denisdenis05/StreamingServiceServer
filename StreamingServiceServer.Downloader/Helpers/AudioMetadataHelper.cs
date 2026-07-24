namespace StreamingServiceDownloader.Helpers;

using StreamingServiceDownloader.Models;

public static class AudioMetadataHelper
{
    public static AudioMetadata? GetAudioMetadata(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            if (file?.Tag == null) return null;

            return new AudioMetadata
            {
                Title = file.Tag.Title,
                Artist = file.Tag.FirstPerformer ?? file.Tag.AlbumArtists?.FirstOrDefault(),
                Album = file.Tag.Album,
                TrackNumber = file.Tag.Track,
                Year = file.Tag.Year
            };
        }
        catch
        {
            return null;
        }
    }

    public static bool IsMetadataMatch(AudioMetadata? metadata, string releaseTitle, string targetTrackTitle)
    {
        if (metadata == null || string.IsNullOrWhiteSpace(metadata.Title))
            return false;

        string sanitizedAlbumTarget = StringSanitizers.SanitizeTitle(releaseTitle).ToLowerInvariant();
        string sanitizedTrackTarget = StringSanitizers.SanitizeTitle(targetTrackTitle).ToLowerInvariant();

        string tagTitleSanitized = StringSanitizers.SanitizeTitle(metadata.Title).ToLowerInvariant();
        string tagAlbumSanitized = StringSanitizers.SanitizeTitle(metadata.Album).ToLowerInvariant();

        int albumScore = !string.IsNullOrWhiteSpace(tagAlbumSanitized) 
            ? FuzzySharp.Fuzz.PartialRatio(sanitizedAlbumTarget, tagAlbumSanitized) 
            : 0;

        int titleScore = FuzzySharp.Fuzz.PartialRatio(sanitizedTrackTarget, tagTitleSanitized);

        return (albumScore > 65 && titleScore > 65) || ((string.IsNullOrWhiteSpace(sanitizedAlbumTarget) || string.IsNullOrWhiteSpace(tagAlbumSanitized)) && titleScore > 75);
    }
}
