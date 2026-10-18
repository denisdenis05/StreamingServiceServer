using System.Text.Json;
using StreamingServiceDownloader.Models.Slskd;

namespace StreamingServiceDownloader.Helpers;

public sealed record SlskdSourceIdentity(Guid SearchId, string Username, string DirectoryPath,
    IReadOnlyList<SlskdFileDownloadRequest> Files)
{
    private const string VersionPrefix = "slskd:v2:";

    public static string DisplayName(string source) => Parse(source) is { } identity
        ? $"slskd:{identity.SearchId}:{identity.Username}" : source;

    public string Encode() => VersionPrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));

    public static SlskdSourceIdentity? Parse(string source, string? legacyDirectory = null)
    {
        if (source.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            try
            {
                var result = JsonSerializer.Deserialize<SlskdSourceIdentity>(Convert.FromBase64String(source[VersionPrefix.Length..]));
                return result is not null && result.SearchId != Guid.Empty && !string.IsNullOrWhiteSpace(result.Username)
                    && result.Files is not null ? result : null;
            }
            catch (Exception failure) when (failure is FormatException or JsonException) { return null; }
        }
        var parts = source.Split(':', 3);
        return parts.Length == 3 && parts[0].Equals("slskd", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(parts[1], out var searchId) && !string.IsNullOrWhiteSpace(parts[2])
            ? new SlskdSourceIdentity(searchId, parts[2], legacyDirectory ?? string.Empty, []) : null;
    }

    public static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');
    public static string DirectoryOf(string filename)
    {
        var normalized = NormalizePath(filename);
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    public bool Matches(SlskdTransferFile directory, SlskdTransferItem file)
    {
        if (string.IsNullOrWhiteSpace(file.Filename)) return false;
        var filename = NormalizePath(file.Filename);
        if (Files.Count > 0)
            return Files.Any(expected => (file.Size == 0 || expected.Size == file.Size)
                && (NormalizePath(expected.Filename) == filename
                    || (!filename.Contains('/') && NormalizePath(directory.Directory ?? string.Empty) == NormalizePath(DirectoryPath)
                        && NormalizePath(expected.Filename).Split('/')[^1] == filename)));
        return !string.IsNullOrWhiteSpace(DirectoryPath)
            && (NormalizePath(directory.Directory ?? string.Empty) == NormalizePath(DirectoryPath)
                || DirectoryOf(filename) == NormalizePath(DirectoryPath));
    }

    public IReadOnlyList<SlskdFileDownloadRequest> MissingFiles(IReadOnlyList<SlskdTransferUser> downloads)
    {
        var user = downloads.FirstOrDefault(candidate => candidate.Username == Username);
        return Files.Where(expected => !(user?.Directories ?? []).Any(directory => (directory.Files ?? [])
            .Any(file => (this with { Files = new[] { expected } }).Matches(directory, file)
                && !IsFailed(file)))).ToArray();
    }

    public static bool IsFailed(SlskdTransferItem file) =>
        file.State?.Contains("Errored", StringComparison.OrdinalIgnoreCase) == true
        || file.State?.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) == true
        || file.State?.Contains("Canceled", StringComparison.OrdinalIgnoreCase) == true;
}
