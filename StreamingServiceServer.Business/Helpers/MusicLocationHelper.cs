using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceServer.Business.Helpers;

public static class MusicLocationHelper
{
    public static string GetRelativeFolderPathForRecording(RecordingResponse recording)
        => GetRelativeFolderPathForRecording(recording.ArtistName, recording.ReleaseTitle);

    public static string GetRelativeFolderPathForRecording(string artistName, string releaseTitle)
    {
        var artistFolder = artistName.Replace(" ", "%").ToUpperInvariant();
        var albumFolder = releaseTitle.Replace(" ", "%").ToUpperInvariant();

        var relativeFolder = $"{artistFolder}/{albumFolder}/";
        if (Path.IsPathRooted(relativeFolder)
            || relativeFolder.Split('/', '\\').Contains(".."))
            throw new InvalidOperationException("Recording folder is outside music storage.");
        return relativeFolder;
    }

    public static IEnumerable<string> EnumerateRecordingFiles(string musicRoot, RecordingResponse recording)
    {
        File.GetAttributes(musicRoot);
        var root = Path.GetFullPath(musicRoot);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        var preferred = GetRelativeFolderPathForRecording(recording);
        var legacy = Path.Combine(SanitizeFolderName(recording.ArtistName).ToUpperInvariant(),
            SanitizeFolderName(recording.ReleaseTitle).ToUpperInvariant());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativeFolder in new[] { preferred, legacy }.Distinct(StringComparer.Ordinal))
        {
            var folder = Path.GetFullPath(Path.Combine(root, relativeFolder));
            if (!folder.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidOperationException("Recording folder is outside music storage.");
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder))
                if (seen.Add(Path.GetFileName(file))) yield return file;
        }
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
