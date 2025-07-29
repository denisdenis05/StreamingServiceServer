using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class RecordingArtistCreditConfiguration: IEntityTypeConfiguration<RecordingArtistCredit>
{
    public void Configure(EntityTypeBuilder<RecordingArtistCredit> builder)
    {
        builder.HasOne(rac => rac.Artist)
            .WithMany()
            .HasForeignKey(rac => rac.ArtistId)
            .OnDelete(DeleteBehavior.SetNull);


    }
}