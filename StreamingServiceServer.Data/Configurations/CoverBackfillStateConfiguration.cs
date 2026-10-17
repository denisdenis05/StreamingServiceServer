using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class CoverBackfillStateConfiguration : IEntityTypeConfiguration<CoverBackfillState>
{
    public void Configure(EntityTypeBuilder<CoverBackfillState> builder)
    {
        builder.HasKey(state => state.EntityKind);
        builder.Property(state => state.Version).IsConcurrencyToken();
    }
}
