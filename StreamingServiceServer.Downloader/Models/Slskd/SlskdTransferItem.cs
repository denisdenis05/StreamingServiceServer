namespace StreamingServiceDownloader.Models.Slskd;

public class SlskdTransferItem
{
    public string? Id { get; set; }
    public string? Filename { get; set; }
    public string? State { get; set; }
    public long Size { get; set; }
    public long BytesTransferred { get; set; }
    public double? PercentComplete { get; set; }
}
