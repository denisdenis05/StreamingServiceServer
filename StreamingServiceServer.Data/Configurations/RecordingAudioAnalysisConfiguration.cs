using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class RecordingAudioAnalysisConfiguration : IEntityTypeConfiguration<RecordingAudioAnalysis>
{
    public void Configure(EntityTypeBuilder<RecordingAudioAnalysis> builder)
    {
        builder.HasKey(analysis => analysis.RecordingId);
        builder.HasOne<Recording>().WithOne().HasForeignKey<RecordingAudioAnalysis>(analysis => analysis.RecordingId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(analysis => analysis.SourceVersion).HasMaxLength(AudioAnalysisStoragePolicy.SourceVersionMaximumLength);
        builder.Property(analysis => analysis.AudioSha256).HasMaxLength(AudioAnalysisStoragePolicy.Sha256Length);
        builder.Property(analysis => analysis.Profile).HasMaxLength(AudioAnalysisStoragePolicy.ProfileMaximumLength);
        builder.Property(analysis => analysis.EngineName).HasMaxLength(AudioAnalysisStoragePolicy.EngineNameMaximumLength);
        builder.Property(analysis => analysis.EngineVersion).HasMaxLength(AudioAnalysisStoragePolicy.EngineVersionMaximumLength);
        builder.Property(analysis => analysis.MusicalKey).HasMaxLength(AudioAnalysisStoragePolicy.MusicalKeyMaximumLength);
        builder.Property(analysis => analysis.Scale).HasMaxLength(AudioAnalysisStoragePolicy.ScaleMaximumLength);
        builder.Property(analysis => analysis.ModelsJson).HasColumnType(AudioAnalysisStoragePolicy.JsonColumnType);
        builder.Property(analysis => analysis.EmbeddingJson).HasColumnType(AudioAnalysisStoragePolicy.JsonColumnType);
        builder.Property(analysis => analysis.FeaturesJson).HasColumnType(AudioAnalysisStoragePolicy.JsonColumnType);
        builder.HasIndex(analysis => new { analysis.Profile, analysis.AnalyzedAt });
    }
}
