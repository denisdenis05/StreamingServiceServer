namespace StreamingServiceDownloader.Models.Slskd;

public class SlskdFileDownloadRequest
{
    public string Filename { get; set; } = string.Empty;
    public long Size { get; set; }
}
