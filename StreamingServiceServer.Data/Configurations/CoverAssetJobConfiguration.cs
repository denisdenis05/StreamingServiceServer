using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;
namespace StreamingServiceServer.Data.Configurations;
public sealed class CoverAssetJobConfiguration : IEntityTypeConfiguration<CoverAssetJob>
{
    public void Configure(EntityTypeBuilder<CoverAssetJob> builder)
    {
        builder.HasKey(job => new { job.EntityKind, job.EntityIdentifier });
        builder.Property(job => job.Version).IsConcurrencyToken();
        builder.HasIndex(job => new { job.CompletedAt, job.NextAttemptAt });
    }
}
