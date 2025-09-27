namespace StreamingServiceServer.Business.Models.LastFm;

public class UserSessionInfo
{
    public string? SessionKey { get; set; }
    public string? Username { get; set; }
    public bool IsValid { get; set; }
    public bool RequiresReauth { get; set; }
}