namespace StreamingServiceDownloader.Models;

public class DownloadProgressInfo
{
    public double Progress { get; set; }
    public string SavePath { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}
