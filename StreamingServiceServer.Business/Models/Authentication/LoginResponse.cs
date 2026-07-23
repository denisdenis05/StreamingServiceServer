namespace StreamingServiceServer.Business.Models.Authentication;

public class LoginResponse
{
    public Guid Id { get; set; }
    public string Username { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}