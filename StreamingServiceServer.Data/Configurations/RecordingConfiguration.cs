using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class RecordingConfiguration: IEntityTypeConfiguration<Recording>
{
    public void Configure(EntityTypeBuilder<Recording> builder)
    {
        builder.HasIndex(a => a.Title);
    }
}