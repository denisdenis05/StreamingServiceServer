using StreamingServiceDownloader.Models.Slskd;

namespace StreamingServiceDownloader.Helpers;

public static class SlskdSourceSelection
{
    public const int MaximumFiles = 1000;
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".flac", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".alac", ".wma", ".opus" };

    public static SlskdFileDownloadRequest[] GetFiles(SlskdSearchResult search, string username, string directoryPath) =>
        (search.Responses ?? []).Where(response => response.Username == username)
            .SelectMany(response => response.Files ?? [])
            .Where(file => !file.IsLocked && !string.IsNullOrWhiteSpace(file.Filename) && file.Size > 0
                && SlskdSourceIdentity.DirectoryOf(file.Filename) == SlskdSourceIdentity.NormalizePath(directoryPath)
                && AudioExtensions.Contains(Path.GetExtension(file.Filename)))
            .GroupBy(file => file.Filename, StringComparer.Ordinal).Select(group => group.First())
            .Select(file => new SlskdFileDownloadRequest { Filename = file.Filename!, Size = file.Size }).ToArray();

    public static bool MatchesFiles(IReadOnlyList<SlskdFileDownloadRequest> expected,
        IReadOnlyList<SlskdFileDownloadRequest> requested) =>
        expected.Count > 0 && expected.Count <= MaximumFiles && expected.Count == requested.Count
        && requested.Select(file => file.Filename).Distinct(StringComparer.Ordinal).Count() == expected.Count
        && requested.All(file => expected.Any(candidate => candidate.Filename == file.Filename && candidate.Size == file.Size));
}
