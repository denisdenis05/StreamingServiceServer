namespace StreamingServiceDownloader.Models.Slskd;

public sealed class SlskdDirectory
{
    public string Name { get; set; } = string.Empty;
    public List<SlskdFile>? Files { get; set; }
}
