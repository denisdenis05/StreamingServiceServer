namespace StreamingServiceDownloader.Models.Slskd;

public class SlskdSearchResponse
{
    public string? Username { get; set; }
    public int FreeUploadSlots { get; set; }
    public long UploadSpeed { get; set; }
    public int FileCount { get; set; }
    public List<SlskdFile>? Files { get; set; }
}
