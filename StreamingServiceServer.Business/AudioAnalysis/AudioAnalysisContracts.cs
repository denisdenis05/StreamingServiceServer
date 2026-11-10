namespace StreamingServiceServer.Business.AudioAnalysis;

public static class AudioAnalysisProtocol
{
    public const int Version = 1;
    public const int FeatureSchemaVersion = 1;
    public const int EmbeddingDimensions = 200;
    public const int MaximumResponseBytes = 131072;
    public const long MaximumAudioBytes = 536870912;
    public const string Profile = "musicnn-v1";
    public const string EngineName = "AudioMuse-AI";
    public const string HealthPath = "health";
    public const string AnalysisPath = "v1/analyze";
    public const string AudioField = "audio";
    public const string RequestField = "request_id";
    public const string ProfileField = "profile";
    public const string AuthorizationScheme = "Bearer";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);
}

public sealed record AudioAnalysisConfiguration(bool Enabled, string EndpointUrl, string ApiToken, string Profile, Guid Revision);

public interface IAudioAnalysisSettingsStore
{
    Task<AudioAnalysisConfiguration> GetConfigurationAsync(CancellationToken cancellationToken);
}

public interface IAudioAnalysisSecretStore
{
    string Protect(string value);
    string Unprotect(string value);
}

public interface IAudioAnalysisClient
{
    Task<bool> CheckHealthAsync(AudioAnalysisConfiguration configuration, CancellationToken cancellationToken);
    Task<AudioAnalysisResult> AnalyzeAsync(AudioAnalysisSource source, Guid requestId, AudioAnalysisConfiguration configuration, CancellationToken cancellationToken);
}

public interface IAudioAnalysisSource
{
    Task<AudioAnalysisSource?> OpenAsync(Guid recordingId, CancellationToken cancellationToken);
}

public sealed class AudioAnalysisSource(Stream content, string sourceVersion, string audioSha256, string extension) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public string SourceVersion { get; } = sourceVersion;
    public string AudioSha256 { get; } = audioSha256;
    public string Extension { get; } = extension;
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record AudioAnalysisResult(int ProtocolVersion, int FeatureSchemaVersion, string EngineName, string EngineVersion,
    string Profile, string AudioSha256, string ModelsJson, string EmbeddingJson, string FeaturesJson,
    int EmbeddingDimensions, double DurationSeconds, double? Tempo, double? Energy, string? MusicalKey, string? Scale);

public sealed class AudioAnalysisException(string failureCode) : Exception(failureCode)
{
    public string FailureCode { get; } = failureCode;
}
