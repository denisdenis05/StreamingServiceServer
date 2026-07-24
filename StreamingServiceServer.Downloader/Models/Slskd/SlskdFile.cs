namespace StreamingServiceDownloader.Models.Slskd;

public class SlskdFile
{
    public string? Filename { get; set; }
    public long Size { get; set; }
    public int? BitRate { get; set; }
    public int? SampleRate { get; set; }
    public int? BitDepth { get; set; }
    public string? Extension { get; set; }
    public bool IsLocked { get; set; }
}
