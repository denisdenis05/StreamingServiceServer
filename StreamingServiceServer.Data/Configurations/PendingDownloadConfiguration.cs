using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class PendingDownloadConfiguration : IEntityTypeConfiguration<PendingDownload>
{
    public void Configure(EntityTypeBuilder<PendingDownload> builder) =>
        builder.Property(download => download.ImportIdentifier).HasDefaultValueSql("gen_random_uuid()");
}
