namespace StreamingServiceServer.Business.Models.Dashboard;

public class TestProviderRequest
{
    public string ProviderName { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
}
