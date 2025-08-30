using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SocialMedia.Data.Models;
using SocialMedia.Data.Models.Enums;

namespace SocialMedia.Data.Configurations;

public class UsersConfiguration: IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);

        builder.HasIndex(u => u.Email)
            .IsUnique();

        builder.HasIndex(u => u.Username)
            .IsUnique();

        builder.HasIndex(u => u.IsEmailConfirmed);

        builder.HasIndex(u => new { u.Provider, u.ProviderId });

        builder.Property(u => u.Email)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(u => u.Username)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(u => u.PasswordHash)
            .IsRequired();

        builder.Property(u => u.ProviderId)
            .HasMaxLength(128);

        builder.Property(u => u.IsEmailConfirmed)
            .HasDefaultValue(false);

        builder.Property(u => u.RoleStatus)
            .HasDefaultValue(RoleStatus.Unverified);
    }
}
