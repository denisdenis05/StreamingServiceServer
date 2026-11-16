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
        List<(string fileName, string fullPath)> files,
        Func<string, AudioMetadata?>? readMetadata = null,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<MusicFileMatch>();
        var unmatchedFiles = files.DistinctBy(file => Path.GetFullPath(file.fullPath), StringComparer.Ordinal).ToList();
        var metadataByPath = new Dictionary<string, AudioMetadata?>(StringComparer.Ordinal);
        foreach (var candidate in unmatchedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            metadataByPath[candidate.fullPath] = (readMetadata ?? AudioMetadataHelper.GetAudioMetadata)(candidate.fullPath);
        }

        foreach (var recording in recordings.OrderBy(r => r.PositionInAlbum))
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string fileName, string fullPath)? bestFile = null;
            int bestScore = 0;
            foreach (var file in unmatchedFiles)
            {
                var score = AudioMatchPolicy.TrackScore(recording, file.fileName, metadataByPath[file.fullPath]);
                if (score <= bestScore) continue;
                bestScore = score;
                bestFile = file;
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
        return MatchSongsWithRecordings(recordings, files, _ => null);
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

}
