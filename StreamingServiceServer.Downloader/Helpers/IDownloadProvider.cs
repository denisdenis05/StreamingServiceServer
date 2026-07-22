using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Helpers;

public interface IDownloadProvider
{
    string ProviderName { get; }
    
    /// <summary>
    /// Search for an album and enqueue it for download.
    /// Returns a result with source identifier on success, null if no match found.
    /// </summary>
    Task<DownloadSearchResult?> SearchAndEnqueueAsync(
        ReleaseToDownload release, 
        string[] formatOrder, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Check download progress for a pending item.
    /// </summary>
    Task<DownloadProgressInfo?> CheckProgressAsync(
        string sourceIdentifier, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Clean up after a completed or cancelled download.
    /// </summary>
    Task CleanUpAsync(string sourceIdentifier, bool deleteFiles);
}

public class DownloadSearchResult
{
    public bool Success { get; set; }
    public string SourceIdentifier { get; set; } = string.Empty;
    public string? SavePath { get; set; }
}

public class DownloadProgressInfo
{
    public double Progress { get; set; }
    public string SavePath { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}
