using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Data.Configurations;

public class UserSavedAlbumConfiguration : IEntityTypeConfiguration<UserSavedAlbum>
{
    public void Configure(EntityTypeBuilder<UserSavedAlbum> builder)
    {
        builder.HasKey(x => new { x.UserId, x.ReleaseId });

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Release)
            .WithMany()
            .HasForeignKey(x => x.ReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.SavedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}
