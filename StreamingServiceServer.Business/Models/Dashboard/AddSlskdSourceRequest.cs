namespace StreamingServiceServer.Business.Models.Dashboard;

public sealed class AddSlskdSourceRequest : ChangeSlskdSourceRequest
{
    public Guid SearchIdentifier { get; set; }
}

public sealed class SlskdSourceSearchRequest
{
    public string? Query { get; set; }
}
