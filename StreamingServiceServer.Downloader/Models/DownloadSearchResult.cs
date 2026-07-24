namespace StreamingServiceDownloader.Models;

public class DownloadSearchResult
{
    public bool Success { get; set; }
    public string SourceIdentifier { get; set; } = string.Empty;
    public string? SavePath { get; set; }
}
