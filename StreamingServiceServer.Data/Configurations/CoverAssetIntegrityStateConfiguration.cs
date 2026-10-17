using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class CoverAssetIntegrityStateConfiguration : IEntityTypeConfiguration<CoverAssetIntegrityState>
{
    public void Configure(EntityTypeBuilder<CoverAssetIntegrityState> builder)
    {
        builder.HasKey(asset => asset.Identity);
        builder.HasIndex(asset => asset.NextVerificationAt);
    }
}
