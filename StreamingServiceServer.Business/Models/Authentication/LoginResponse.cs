namespace StreamingServiceServer.Business.Models.Authentication;

public class LoginResponse
{
    public Guid Id { get; set; }
    public string Username { get; set; }
    public string Token { get; set; }
}