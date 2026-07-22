namespace StreamingServiceServer.Data.Models;

public class PendingDownload
{
    public Guid Id { get; set; }
    public string SourceName { get; set; } = string.Empty;
    
    /// <summary>"Torrent" or "Soulseek"</summary>
    public string ProviderName { get; set; } = "Torrent";
    
    /// <summary>For Soulseek: the local directory where files are being downloaded</summary>
    public string? SavePath { get; set; }
}