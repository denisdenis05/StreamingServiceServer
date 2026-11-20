using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;

public sealed class RecordingLyricsConfiguration : IEntityTypeConfiguration<RecordingLyrics>
{
    public void Configure(EntityTypeBuilder<RecordingLyrics> builder)
    {
        builder.HasKey(lyrics => lyrics.RecordingIdentifier);
        builder.HasOne<Recording>().WithOne().HasForeignKey<RecordingLyrics>(lyrics => lyrics.RecordingIdentifier).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(lyrics => new { lyrics.NextAttemptAt, lyrics.RecordingIdentifier });
        builder.HasIndex(lyrics => new { lyrics.LookupVersion, lyrics.State, lyrics.LastFailure, lyrics.NextAttemptAt });
        builder.Property(lyrics => lyrics.LookupVersion).HasDefaultValue(LyricsLookupVersions.Original);
        builder.Property(lyrics => lyrics.LeaseToken).IsConcurrencyToken();
    }
}

public sealed class LyricsQueueStateConfiguration : IEntityTypeConfiguration<LyricsQueueState>
{
    public void Configure(EntityTypeBuilder<LyricsQueueState> builder) => builder.HasKey(state => state.Provider);
}
