using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialMedia.Data.Models;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class RecommendationPlaylistCacheConfiguration : IEntityTypeConfiguration<RecommendationPlaylistCache>
{
    public void Configure(EntityTypeBuilder<RecommendationPlaylistCache> builder)
    {
        builder.HasKey(cache => new { cache.UserId, cache.CacheKey });
        builder.Property(cache => cache.CacheKey).HasMaxLength(64);
        builder.Property(cache => cache.Mode).HasMaxLength(32);
        builder.Property(cache => cache.Payload).HasColumnType("jsonb");
        builder.HasIndex(cache => cache.ExpiresAt);
        builder.HasOne<User>().WithMany().HasForeignKey(cache => cache.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
