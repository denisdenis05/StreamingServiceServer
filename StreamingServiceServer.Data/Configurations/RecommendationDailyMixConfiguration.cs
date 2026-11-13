using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialMedia.Data.Models;
using StreamingServiceServer.Data.Models.Library;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class RecommendationDailyMixConfiguration : IEntityTypeConfiguration<RecommendationDailyMix>
{
    public void Configure(EntityTypeBuilder<RecommendationDailyMix> builder)
    {
        builder.HasKey(mix => new { mix.UserId, mix.Day });
        builder.HasOne<User>().WithMany().HasForeignKey(mix => mix.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Playlist>().WithMany().HasForeignKey(mix => mix.SavedPlaylistId).OnDelete(DeleteBehavior.SetNull);
        builder.Property(mix => mix.Version).IsConcurrencyToken();
    }
}
