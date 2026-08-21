namespace StreamingServiceServer.Business.Models.Dashboard;

public class ChangeSlskdSourceRequest
{
    public string Username { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public List<ChangeSlskdSourceFile> Files { get; set; } = new();
}

public class ChangeSlskdSourceFile
{
    public string Filename { get; set; } = string.Empty;
    public long Size { get; set; }
}
