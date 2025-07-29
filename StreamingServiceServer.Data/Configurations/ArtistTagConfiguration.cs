using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ArtistTagConfiguration: IEntityTypeConfiguration<ArtistTag>
{
    public void Configure(EntityTypeBuilder<ArtistTag> builder)
    {
        builder.HasOne(at => at.Artist)
            .WithMany(a => a.Tags)
            .HasForeignKey(at => at.ArtistId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(at => at.Name);
    }
}