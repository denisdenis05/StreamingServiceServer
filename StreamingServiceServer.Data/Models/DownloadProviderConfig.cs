namespace StreamingServiceServer.Data.Models;

public class DownloadProviderConfig
{
    public Guid Id { get; set; }


    public string ProviderName { get; set; } = string.Empty;


    public int PriorityOrder { get; set; }

    public bool IsEnabled { get; set; } = true;


    public string ApiUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;


    public string FormatPreferences { get; set; } = "FLAC,WAV,MP3,ANY";


    public string ExtraConfig { get; set; } = "{}";

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
