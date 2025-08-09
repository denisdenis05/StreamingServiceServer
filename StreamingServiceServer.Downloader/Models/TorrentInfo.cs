namespace StreamingServiceDownloader.Models;

public class TorrentInfo
{
    public string Hash { get; set; }
    public string Name { get; set; }
    public double Progress { get; set; } // 0.0 to 1.0
    public string SavePath { get; set; }
    public long Size { get; set; }
    public long Downloaded { get; set; }
    public long Uploaded { get; set; }
    public string State { get; set; }
}