namespace StreamingServiceDownloader.Models;

public class TorrentResult
{
    public string Title { get; set; }
    public string Uploaded { get; set; }
    public string Size { get; set; }
    public int Seeders { get; set; }
    public int Leechers { get; set; }
    public string Uploader { get; set; }
    public string MagnetLink { get; set; }
}