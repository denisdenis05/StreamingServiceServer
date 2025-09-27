namespace StreamingServiceServer.Business.Models.LastFm;

public class LastFmAuthRequest
{
    public string Token { get; set; }
    public Guid UserId { get; set; }
}
