using StreamingServiceDownloader.Models;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Helpers;

public interface IDownloadProvider
{
    string ProviderName { get; }

    Task<DownloadSearchResult?> SearchAndEnqueueAsync(
        ReleaseToDownload release, 
        string[] formatOrder, 
        CancellationToken cancellationToken);

    Task<DownloadProgressInfo?> CheckProgressAsync(
        string sourceIdentifier, 
        CancellationToken cancellationToken);

    Task CleanUpAsync(string sourceIdentifier, bool deleteFiles);
}
