using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Data.Configurations;


public class PlaylistRecordingConfiguration : IEntityTypeConfiguration<PlaylistRecording>
{
    public void Configure(EntityTypeBuilder<PlaylistRecording> builder)
    {
        builder.HasKey(pr => new { pr.PlaylistId, pr.RecordingId });

        builder.HasOne(pr => pr.Playlist)
            .WithMany(p => p.PlaylistRecordings)
            .HasForeignKey(pr => pr.PlaylistId);

        builder.HasOne(pr => pr.Recording)
            .WithMany(r => r.PlaylistRecordings) 
            .HasForeignKey(pr => pr.RecordingId);

        builder.Property(pr => pr.Order)
            .IsRequired();

        builder.Property(pr => pr.AddedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.HasIndex(pr => pr.Order); 
    }
}