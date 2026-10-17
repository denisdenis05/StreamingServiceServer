using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class ReleaseConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> builder) => builder.HasIndex(release => new { release.DateAdded, release.Id });
}
