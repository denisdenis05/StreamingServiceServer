using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class AudioAnalysisSettingsConfiguration : IEntityTypeConfiguration<AudioAnalysisSettings>
{
    private const string SingletonConstraintName = "CK_AudioAnalysisSettings_Singleton";
    private const string SingletonConstraintSql = "\"Id\" = 1";

    public void Configure(EntityTypeBuilder<AudioAnalysisSettings> builder)
    {
        builder.HasKey(settings => settings.Id);
        builder.Property(settings => settings.Id).ValueGeneratedNever();
        builder.ToTable(table => table.HasCheckConstraint(SingletonConstraintName, SingletonConstraintSql));
        builder.Property(settings => settings.EndpointUrl).HasMaxLength(AudioAnalysisStoragePolicy.EndpointUrlMaximumLength);
        builder.Property(settings => settings.ProtectedApiToken).HasMaxLength(AudioAnalysisStoragePolicy.ProtectedApiTokenMaximumLength);
        builder.Property(settings => settings.Profile).HasMaxLength(AudioAnalysisStoragePolicy.ProfileMaximumLength);
        builder.Property(settings => settings.Revision).IsConcurrencyToken();
        builder.HasData(new AudioAnalysisSettings
        {
            Revision = AudioAnalysisStoragePolicy.InitialSettingsRevision,
            UpdatedAt = DateTimeOffset.UnixEpoch
        });
    }
}
