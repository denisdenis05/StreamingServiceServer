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
    /// Matches a flat list of audio files against album recordings using audio tag metadata
    /// (preferred) or filename fuzzy matching as fallback. Returns matches keyed by RecordingResponse.Id.
    /// </summary>
    public static async Task<ICollection<MusicFileMatch>> MatchSongsWithMetadataAsync(
        Guid albumId,
        List<(string fileName, string fullPath)> files,
        IExternalMusicSearchService externalMusicSearchService,
        IImageService imageService)
    {
        var recordings = await externalMusicSearchService.SearchAlbumRecordingsByIdAsync(albumId);
        var simpleRecordings = recordings.Select(r => r.ToResponse(imageService)).ToList();
        return MatchSongsWithRecordings(simpleRecordings, files);
    }

    /// <summary>
    /// Synchronous matching against pre-loaded DB recordings (RecordingResponse list),
    /// producing MusicFileMatch items keyed directly by Recording.Id.
    /// </summary>
    public static ICollection<MusicFileMatch> MatchSongsWithRecordings(
        IEnumerable<RecordingResponse> recordings,
        List<(string fileName, string fullPath)> files)
    {
        var matches = new List<MusicFileMatch>();
        var unmatchedFiles = new List<(string fileName, string fullPath)>(files);

        foreach (var recording in recordings.OrderBy(r => r.PositionInAlbum))
        {
            string targetTitle = recording.Title ?? "";
            string sanitizedTargetTitle = StringSanitizers.SanitizeTitle(targetTitle).ToLowerInvariant();
            string targetPosTitle = $"{recording.PositionInAlbum} {targetTitle}";
            string sanitizedTargetPosTitle = StringSanitizers.SanitizeTitle(targetPosTitle).ToLowerInvariant();

            (string fileName, string fullPath)? bestFile = null;
            int bestScore = 0;

            foreach (var (fileName, fullPath) in unmatchedFiles)
            {
                var metadata = AudioMetadataHelper.GetAudioMetadata(fullPath);

                int combinedScore = 0;

                // 1. Prefer full audio tag metadata match
                if (AudioMetadataHelper.IsMetadataMatch(metadata, recording.ReleaseTitle ?? "", targetTitle))
                {
                    combinedScore = 100;
                }
                else if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                {
                    // 2. Fuzzy match against ID3 Title tag
                    string metaTitleClean = StringSanitizers.SanitizeTitle(metadata.Title).ToLowerInvariant();
                    int titleScore = Fuzz.TokenSetRatio(metaTitleClean, sanitizedTargetTitle);
                    int posTitleScore = Fuzz.TokenSetRatio(metaTitleClean, sanitizedTargetPosTitle);
                    combinedScore = Math.Max(titleScore, posTitleScore);
                }
                else
                {
                    // 3. Fallback: filename matching + track number bonus
                    var (fileTrackNum, fileTitle) = ExtractTrackInfo(fileName);
                    bool trackNumMatches = fileTrackNum.HasValue && fileTrackNum == recording.PositionInAlbum;

                    int titleScore = Fuzz.TokenSetRatio(fileTitle, sanitizedTargetTitle);
                    int posTitleScore = Fuzz.TokenSetRatio(fileTitle, sanitizedTargetPosTitle);
                    combinedScore = Math.Max(titleScore, posTitleScore);

                    if (trackNumMatches)
                        combinedScore += 15;
                }

                if (combinedScore > bestScore && combinedScore >= 50)
                {
                    bestScore = combinedScore;
                    bestFile = (fileName, fullPath);
                }
            }

            // Fallback 1: Match by extracted track number in filename (e.g. "01.mp3", "Track 01.flac")
            if (bestFile == null)
            {
                foreach (var (fileName, fullPath) in unmatchedFiles)
                {
                    var (fileTrackNum, _) = ExtractTrackInfo(fileName);
                    if (fileTrackNum.HasValue && fileTrackNum.Value == recording.PositionInAlbum)
                    {
                        bestFile = (fileName, fullPath);
                        bestScore = 70; // High confidence position match
                        break;
                    }
                }
            }

            // Fallback 2: Sequential fallback if unmatched files remain
            if (bestFile == null && unmatchedFiles.Count > 0)
            {
                var sortedFiles = unmatchedFiles.OrderBy(f => f.fileName, StringComparer.OrdinalIgnoreCase).ToList();
                // Find file at position index or first remaining
                var candidate = sortedFiles.FirstOrDefault(f =>
                {
                    var (fileTrackNum, _) = ExtractTrackInfo(f.fileName);
                    return fileTrackNum.HasValue && fileTrackNum.Value == recording.PositionInAlbum;
                });

                if (candidate != default)
                {
                    bestFile = candidate;
                    bestScore = 60;
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
    /// Matches audio files by evaluating filename combinations (excluding extension)
    /// after stripping noise like [], (), -, _, and testing multiple pattern combinations:
    /// (Artist + Title, Title + Artist, Title, Position + Title, Position + Artist + Title).
    /// </summary>
    public static ICollection<MusicFileMatch> MatchSongsByFilenameCombinations(
        IEnumerable<RecordingResponse> recordings,
        List<(string fileName, string fullPath)> files)
    {
        var matches = new List<MusicFileMatch>();
        var unmatchedFiles = new List<(string fileName, string fullPath)>(files);

        foreach (var recording in recordings.OrderBy(r => r.PositionInAlbum))
        {
            string titleClean = CleanFilenamePart(recording.Title ?? "");
            string posStr = recording.PositionInAlbum > 0 ? recording.PositionInAlbum.ToString() : "";
            string posPadded = recording.PositionInAlbum > 0 ? recording.PositionInAlbum.ToString("D2") : "";

            var patterns = new List<string>
            {
                titleClean,
                $"{posStr} {titleClean}",
                $"{posPadded} {titleClean}"
            };

            (string fileName, string fullPath)? bestFile = null;
            int bestScore = 0;

            foreach (var file in unmatchedFiles)
            {
                var fnWithoutExt = Path.GetFileNameWithoutExtension(file.fileName);
                var (fileTrackNum, _) = ExtractTrackInfo(file.fileName);

                // Check if the song title naturally contains this track number (e.g., song title is "48" or "Rule 3")
                bool isTrackNumInTitle = false;
                if (fileTrackNum.HasValue && !string.IsNullOrWhiteSpace(recording.Title))
                {
                    string numStr = fileTrackNum.Value.ToString();
                    string numPadded = fileTrackNum.Value.ToString("D2");
                    isTrackNumInTitle = System.Text.RegularExpressions.Regex.IsMatch(
                        recording.Title, $@"\b({numStr}|{numPadded})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }

                // If track number is in filename but NOT in song title, strip leading track number from filename text
                string fnClean;
                if (fileTrackNum.HasValue && !isTrackNumInTitle)
                {
                    string fnWithoutTrackNum = System.Text.RegularExpressions.Regex.Replace(
                        fnWithoutExt, @"^\s*\d{1,2}\s*[-._ ]\s*", "");
                    fnClean = CleanFilenamePart(fnWithoutTrackNum);
                }
                else
                {
                    fnClean = CleanFilenamePart(fnWithoutExt);
                }

                bool trackNumMatches = fileTrackNum.HasValue && fileTrackNum.Value == recording.PositionInAlbum;

                int fileBestPatternScore = 0;
                foreach (var pattern in patterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;

                    int tokenScore = Fuzz.TokenSetRatio(pattern, fnClean);
                    int partialScore = Fuzz.PartialRatio(pattern, fnClean);
                    int score = Math.Max(tokenScore, partialScore);

                    // Add bonus if track position matches number in filename
                    if (trackNumMatches)
                    {
                        score += 20;
                    }

                    if (score > fileBestPatternScore)
                    {
                        fileBestPatternScore = score;
                    }
                }

                if (fileBestPatternScore > bestScore)
                {
                    bestScore = fileBestPatternScore;
                    bestFile = file;
                }
            }

            if (bestFile != null && bestScore >= 50)
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

    private static string CleanFilenamePart(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Remove brackets [...] and parentheses (...)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\([^\)]*\)", "");
        // Replace hyphens, underscores, dots with spaces
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[-_.]", " ");
        // Remove non-word non-space characters
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[^\w\s]", "");
        // Collapse whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.ToLowerInvariant();
    }

    /// <summary>
    /// Strips brackets/parentheses content from a folder name before fuzzy comparison.
    /// e.g. "[2013] The Lost Tapes" → "The Lost Tapes"
    /// </summary>
    public static string SanitizeFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return string.Empty;
        // Trim trailing slashes first
        folderName = folderName.TrimEnd('/', '\\');
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
