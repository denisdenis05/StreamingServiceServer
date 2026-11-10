using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class AudioAnalysisJobConfiguration : IEntityTypeConfiguration<AudioAnalysisJob>
{
    public void Configure(EntityTypeBuilder<AudioAnalysisJob> builder)
    {
        builder.HasKey(job => job.RecordingId);
        builder.HasOne<Recording>().WithOne().HasForeignKey<AudioAnalysisJob>(job => job.RecordingId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(job => job.Version).IsConcurrencyToken();
        builder.Property(job => job.SourceVersion).HasMaxLength(AudioAnalysisStoragePolicy.SourceVersionMaximumLength);
        builder.Property(job => job.LastFailure).HasMaxLength(AudioAnalysisStoragePolicy.FailureCodeMaximumLength);
        builder.HasIndex(job => new { job.NextAttemptAt, job.RecordingId });
        builder.HasIndex(job => new { job.State, job.NextAttemptAt });
        builder.HasIndex(job => new { job.State, job.LeaseUntil });
    }
}
