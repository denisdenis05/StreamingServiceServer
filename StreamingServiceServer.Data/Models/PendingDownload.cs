namespace StreamingServiceServer.Data.Models;

public class PendingDownload
{
    public Guid Id { get; set; }
    public Guid ImportIdentifier { get; set; } = Guid.NewGuid();
    public string SourceName { get; set; } = string.Empty;


    public string ProviderName { get; set; } = "Torrent";


    public string? SavePath { get; set; }
}
