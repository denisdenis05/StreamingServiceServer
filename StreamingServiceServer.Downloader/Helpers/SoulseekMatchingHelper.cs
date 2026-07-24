using FuzzySharp;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;

namespace StreamingServiceDownloader.Helpers;


public static class SoulseekMatchingHelper
{


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


                if (AudioMetadataHelper.IsMetadataMatch(metadata, recording.ReleaseTitle ?? "", targetTitle))
                {
                    combinedScore = 100;
                }
                else if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                {

                    string metaTitleClean = StringSanitizers.SanitizeTitle(metadata.Title).ToLowerInvariant();
                    int titleScore = Fuzz.TokenSetRatio(metaTitleClean, sanitizedTargetTitle);
                    int posTitleScore = Fuzz.TokenSetRatio(metaTitleClean, sanitizedTargetPosTitle);
                    combinedScore = Math.Max(titleScore, posTitleScore);
                }
                else
                {

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


            if (bestFile == null)
            {
                foreach (var (fileName, fullPath) in unmatchedFiles)
                {
                    var (fileTrackNum, _) = ExtractTrackInfo(fileName);
                    if (fileTrackNum.HasValue && fileTrackNum.Value == recording.PositionInAlbum)
                    {
                        bestFile = (fileName, fullPath);
                        bestScore = 70; 
                        break;
                    }
                }
            }


            if (bestFile == null && unmatchedFiles.Count > 0)
            {
                var sortedFiles = unmatchedFiles.OrderBy(f => f.fileName, StringComparer.OrdinalIgnoreCase).ToList();

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


                bool isTrackNumInTitle = false;
                if (fileTrackNum.HasValue && !string.IsNullOrWhiteSpace(recording.Title))
                {
                    string numStr = fileTrackNum.Value.ToString();
                    string numPadded = fileTrackNum.Value.ToString("D2");
                    isTrackNumInTitle = System.Text.RegularExpressions.Regex.IsMatch(
                        recording.Title, $@"\b({numStr}|{numPadded})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }


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

        text = StringSanitizers.NormalizeSpecialCharacters(text);

        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\([^\)]*\)", "");

        text = System.Text.RegularExpressions.Regex.Replace(text, @"[-_.]", " ");

        text = System.Text.RegularExpressions.Regex.Replace(text, @"[^\w\s]", "");

        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.ToLowerInvariant();
    }


    public static string SanitizeFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return string.Empty;

        folderName = StringSanitizers.NormalizeSpecialCharacters(folderName);

        folderName = folderName.TrimEnd('/', '\\');

        folderName = System.Text.RegularExpressions.Regex.Replace(folderName, @"\[[^\]]*\]", "");
        folderName = System.Text.RegularExpressions.Regex.Replace(folderName, @"\([^)]*\)", "");
        folderName = System.Text.RegularExpressions.Regex.Replace(folderName, @"\{[^}]*\}", "");

        folderName = System.Text.RegularExpressions.Regex.Replace(folderName, @"[-_./\\]", " ");

        folderName = System.Text.RegularExpressions.Regex.Replace(folderName, @"[^\w\s]", "");

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
