using StreamingServiceDownloader.Models.Slskd;

namespace StreamingServiceDownloader.Models.Soulseek;

public class CandidateDirectory
{
    public string Username { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public List<SlskdFileDownloadRequest> Files { get; set; } = new();
    public int Score { get; set; }
    public int FileCount => Files.Count;
    public long UploadSpeed { get; set; }
    public bool HasFreeSlots { get; set; }
}
