using System.Text.Json.Serialization;

namespace StreamingServiceDownloader.Models;

public class TorrentInfo
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("save_path")]
    public string SavePath { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("downloaded")]
    public long Downloaded { get; set; }

    [JsonPropertyName("uploaded")]
    public long Uploaded { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; }
}