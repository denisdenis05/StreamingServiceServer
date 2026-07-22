namespace StreamingServiceServer.Data.Models;

public class DownloadProviderConfig
{
    public Guid Id { get; set; }
    
    /// <summary>"Torrent" or "Soulseek"</summary>
    public string ProviderName { get; set; } = string.Empty;
    
    /// <summary>Lower number = higher priority (1 = primary, 2 = fallback)</summary>
    public int PriorityOrder { get; set; }
    
    public bool IsEnabled { get; set; } = true;
    
    // Connection credentials
    public string ApiUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    
    /// <summary>Comma-separated format preferences, e.g. "FLAC,WAV,MP3,ANY"</summary>
    public string FormatPreferences { get; set; } = "FLAC,WAV,MP3,ANY";
    
    /// <summary>JSON blob for provider-specific settings (e.g. TPB base URL, retry params)</summary>
    public string ExtraConfig { get; set; } = "{}";
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
