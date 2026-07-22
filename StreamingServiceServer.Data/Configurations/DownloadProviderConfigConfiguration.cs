using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public class DownloadProviderConfigConfiguration : IEntityTypeConfiguration<DownloadProviderConfig>
{
    public void Configure(EntityTypeBuilder<DownloadProviderConfig> builder)
    {
        builder.HasKey(e => e.Id);
        
        builder.Property(e => e.ProviderName)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(e => e.ProviderName)
            .IsUnique();

        builder.Property(e => e.ApiUrl).HasMaxLength(500);
        builder.Property(e => e.Username).HasMaxLength(200);
        builder.Property(e => e.Password).HasMaxLength(500);
        builder.Property(e => e.ApiKey).HasMaxLength(500);
        builder.Property(e => e.FormatPreferences).HasMaxLength(200);
        builder.Property(e => e.ExtraConfig).HasColumnType("text");
    }
}
