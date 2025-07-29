using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ArtistAliasConfiguration: IEntityTypeConfiguration<ArtistAlias>
{
    public void Configure(EntityTypeBuilder<ArtistAlias> builder)
    {
        builder.HasOne(aa => aa.Artist)
            .WithMany(a => a.Aliases)
            .HasForeignKey(aa => aa.ArtistId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => a.Name);
    }
}