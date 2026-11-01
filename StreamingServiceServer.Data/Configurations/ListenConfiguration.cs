using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class ListenConfiguration : IEntityTypeConfiguration<Listen>
{
    public void Configure(EntityTypeBuilder<Listen> builder)
    {
        builder.HasIndex(listen => new { listen.UserId, listen.SourceAlbumId, listen.StartTime });
        builder.HasIndex(listen => new { listen.UserId, listen.PlaylistId, listen.StartTime });
    }
}
