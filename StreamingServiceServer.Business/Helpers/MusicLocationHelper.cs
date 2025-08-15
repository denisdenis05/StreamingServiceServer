using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceServer.Business.Helpers;

public static class MusicLocationHelper
{
    public static string GetRelativeFolderPathForRecording(RecordingResponse recording)
    {
        var artistFolder = recording.ArtistName.Replace(" ", "%").ToUpper();
        var albumFolder = recording.ReleaseTitle.Replace(" ", "%").ToUpper();

        return $"{artistFolder}/{albumFolder}/";
    }

    public static string SanitizeFolderName(string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
            return string.Empty;

        var sanitized = originalName.Replace(" ", "%");

        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"[^a-zA-Z0-9%\(\)\[\]\-_.]",
            string.Empty
        );

        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"%+", "%");

        return sanitized;
    }
}