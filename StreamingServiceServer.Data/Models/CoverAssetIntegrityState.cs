namespace StreamingServiceServer.Data.Models;

public sealed class CoverAssetIntegrityState
{
    public string Identity { get; set; } = string.Empty;
    public DateTimeOffset NextVerificationAt { get; set; }
}
