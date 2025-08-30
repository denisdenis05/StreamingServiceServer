namespace StreamingServiceServer.Business.Models.Authentication;

public class RefreshTokenResponse
{
    public string Token { get; set; }
    public string RefreshToken { get; set; }
}