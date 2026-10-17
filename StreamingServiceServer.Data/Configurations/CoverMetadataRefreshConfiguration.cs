using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class CoverMetadataRefreshConfiguration : IEntityTypeConfiguration<CoverMetadataRefresh>
{
    public void Configure(EntityTypeBuilder<CoverMetadataRefresh> builder)
    {
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Version).IsConcurrencyToken();
    }
}
