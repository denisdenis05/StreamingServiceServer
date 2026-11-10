namespace StreamingServiceServer.Data.Models;

public sealed class RecordingAudioAnalysis
{
    public Guid RecordingId { get; set; }
    public string SourceVersion { get; set; } = string.Empty;
    public string AudioSha256 { get; set; } = string.Empty;
    public string Profile { get; set; } = AudioAnalysisStoragePolicy.DefaultProfile;
    public string EngineName { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; }
    public int FeatureSchemaVersion { get; set; }
    public int EmbeddingDimensions { get; set; }
    public string ModelsJson { get; set; } = AudioAnalysisStoragePolicy.EmptyObjectJson;
    public string EmbeddingJson { get; set; } = AudioAnalysisStoragePolicy.EmptyArrayJson;
    public string FeaturesJson { get; set; } = AudioAnalysisStoragePolicy.EmptyObjectJson;
    public double? Tempo { get; set; }
    public double? Energy { get; set; }
    public string? MusicalKey { get; set; }
    public string? Scale { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset AnalyzedAt { get; set; }
}
