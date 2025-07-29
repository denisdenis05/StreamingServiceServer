using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ArtistConfiguration: IEntityTypeConfiguration<Artist>
{
    public void Configure(EntityTypeBuilder<Artist> builder)
    {
        builder.HasIndex(a => a.Name);
    }
}