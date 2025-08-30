namespace StreamingServiceServer.Business.Models.Authentication;

public class LocalLoginRequest
{
    public string EmailOrUsername { get; set; }
    public string Password { get; set; }
    public bool KeepMeLoggedIn { get; set; }
}