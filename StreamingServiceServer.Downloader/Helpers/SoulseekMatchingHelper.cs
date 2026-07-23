using FuzzySharp;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;

namespace StreamingServiceDownloader.Helpers;

/// <summary>
/// Shared helper for matching audio files from a folder to album recordings.
/// Extracted from PendingDownloadChecker so DashboardController can also use it.
/// </summary>
public static class SoulseekMatchingHelper
{
    /// <summary>
    /// Matches a flat list of audio files against the recordings of an album,
    /// using audio tag metadata (preferred) or filename fuzzy matching as fallback.
    /// </summary>
    public static async Task<ICollection<MusicFileMatch>> MatchSongsWithMetadataAsync(
        Guid albumId,
        List<(string fileName, string fullPath)> files,
        IExternalMusicSearchService externalMusicSearchService,
        IImageService imageService)
    {
        var recordings = await externalMusicSearchService.SearchAlbumRecordingsByIdAsync(albumId);
        var simpleRecordings = recordings.Select(r => r.ToResponse(imageService)).ToList();

        var matches = new List<MusicFileMatch>();
        var unmatchedFiles = new List<(string fileName, string fullPath)>(files);

        foreach (var recording in simpleRecordings.OrderBy(r => r.PositionInAlbum))
        {
            string target = $"{recording.PositionInAlbum} {recording.Title}";
            string sanitizedTarget = StringSanitizers.SanitizeTitle(target).ToLowerInvariant();

            (string fileName, string fullPath)? bestFile = null;
            int bestScore = 0;

            foreach (var (fileName, fullPath) in unmatchedFiles)
            {
                var metadata = AudioMetadataHelper.GetAudioMetadata(fullPath);

                int combinedScore = 0;

                if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                {
                    int titleScore = Fuzz.TokenSetRatio(
                        StringSanitizers.SanitizeTitle(metadata.Title).ToLowerInvariant(),
                        sanitizedTarget
                    );
                    combinedScore = titleScore;
                }
                else
                {
                    var (fileTrackNum, fileTitle) = ExtractTrackInfo(fileName);
                    bool trackNumMatches = fileTrackNum.HasValue && fileTrackNum == recording.PositionInAlbum;
                    int titleScore = Fuzz.TokenSetRatio(fileTitle, sanitizedTarget);
                    combinedScore = titleScore;
                    if (trackNumMatches) combinedScore += 15;
                }

                if (combinedScore > bestScore)
                {
                    bestScore = combinedScore;
                    bestFile = (fileName, fullPath);
                }
            }

            if (bestFile != null)
            {
                matches.Add(new MusicFileMatch
                {
                    FileName = bestFile.Value.fileName,
                    FullPath = bestFile.Value.fullPath,
                    Recording = recording,
                    Score = bestScore
                });
                unmatchedFiles.Remove(bestFile.Value);
            }
            else
            {
                matches.Add(new MusicFileMatch
                {
                    FileName = "<NO MATCH>",
                    FullPath = string.Empty,
                    Recording = recording,
                    Score = 0
                });
            }
        }

        return matches;
    }

    /// <summary>
    /// Strips brackets/parentheses content from a folder name before fuzzy comparison.
    /// e.g. "[2013] The Lost Tapes" → "The Lost Tapes"
    /// </summary>
    public static string SanitizeFolderName(string folderName)
    {
        // Remove anything in square brackets (e.g. [2013], [FLAC])
        folderName = System.Text.RegularExpressions.Regex.Replace(folderName, @"\[[^\]]*\]", "");
        // Remove anything in parentheses (e.g. (2013), (Vinyl))
        folderName = System.Text.RegularExpressions.Regex.Replace(folderName, @"\([^)]*\)", "");
        // Collapse whitespace
        folderName = System.Text.RegularExpressions.Regex.Replace(folderName, @"\s+", " ").Trim();
        return folderName;
    }

    private static (int? trackNumber, string cleanTitle) ExtractTrackInfo(string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        var match = System.Text.RegularExpressions.Regex.Match(
            nameWithoutExt,
            @"^(?<num>\d{1,2})\s*[-._ ]?\s*(?<title>.*)$"
        );

        if (match.Success)
        {
            int num = int.Parse(match.Groups["num"].Value);
            string title = match.Groups["title"].Value;
            title = System.Text.RegularExpressions.Regex.Replace(title, @"[^\w\s]", " ");
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+", " ").Trim();
            return (num, title.ToLowerInvariant());
        }
        else
        {
            var clean = System.Text.RegularExpressions.Regex.Replace(nameWithoutExt, @"[^\w\s]", " ");
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
            return (null, clean.ToLowerInvariant());
        }
    }
}
