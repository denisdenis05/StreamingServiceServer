namespace StreamingServiceServer.Data.Models;

public static class AudioAnalysisStoragePolicy
{
    public const int SettingsIdentifier = 1;
    public const string DefaultProfile = "musicnn-v1";
    public const int EndpointUrlMaximumLength = 2048;
    public const int ProtectedApiTokenMaximumLength = 8192;
    public const int ProfileMaximumLength = 128;
    public const int SourceVersionMaximumLength = 256;
    public const int FailureCodeMaximumLength = 128;
    public const int Sha256Length = 64;
    public const int EngineNameMaximumLength = 128;
    public const int EngineVersionMaximumLength = 128;
    public const int MusicalKeyMaximumLength = 32;
    public const int ScaleMaximumLength = 32;
    public const string JsonColumnType = "jsonb";
    public const string EmptyObjectJson = "{}";
    public const string EmptyArrayJson = "[]";
    public static readonly Guid InitialSettingsRevision = Guid.Parse("4980d5bd-497a-4eea-9ce2-2cdba3dfe2ec");
}
