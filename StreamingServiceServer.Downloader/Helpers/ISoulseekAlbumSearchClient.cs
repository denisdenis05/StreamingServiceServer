using StreamingServiceDownloader.Models.Slskd;

namespace StreamingServiceDownloader.Helpers;

public interface ISoulseekAlbumSearchClient
{
    const int SearchTimeoutSeconds = 90;

    Task<Guid?> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<SlskdSearchResult?> WaitForSearchResultsAsync(Guid searchIdentifier, int maximumWaitSeconds = SearchTimeoutSeconds,
        CancellationToken cancellationToken = default);
    Task<List<SlskdFile>> GetDirectoryFilesAsync(string username, string directoryPath,
        CancellationToken cancellationToken);
    Task StartAlbumDownloadAsync(SlskdSourceIdentity source, CancellationToken cancellationToken);
    Task DeleteSearchAsync(Guid searchIdentifier, CancellationToken cancellationToken = default);
}
