namespace StreamingServiceServer.Business.Models.Authentication;

public class LocalRegisterRequest
{
    public string Username { get; set; }
    public string Email { get; set; }
    public string Password { get; set; }
}