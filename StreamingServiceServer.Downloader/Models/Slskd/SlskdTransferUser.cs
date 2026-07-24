namespace StreamingServiceDownloader.Models.Slskd;

public class SlskdTransferUser
{
    public string? Username { get; set; }
    public List<SlskdTransferFile>? Directories { get; set; }
}
