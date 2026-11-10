namespace StreamingServiceServer.Data.Models;

public sealed class AudioAnalysisSettings
{
    public int Id { get; set; } = AudioAnalysisStoragePolicy.SettingsIdentifier;
    public bool Enabled { get; set; }
    public string EndpointUrl { get; set; } = string.Empty;
    public string ProtectedApiToken { get; set; } = string.Empty;
    public string Profile { get; set; } = AudioAnalysisStoragePolicy.DefaultProfile;
    public Guid Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
