namespace StreamingServiceServer.Business.Models.Dashboard;

public class UpdateProviderRequest
{
    public Guid Id { get; set; }
    public int PriorityOrder { get; set; }
    public bool IsEnabled { get; set; }
    public string? ApiUrl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public string? FormatPreferences { get; set; }
    public string? ExtraConfig { get; set; }
}
