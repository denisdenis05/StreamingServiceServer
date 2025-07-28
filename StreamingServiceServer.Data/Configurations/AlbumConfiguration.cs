using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ArticlesConfiguration: IEntityTypeConfiguration<Album>
{
    public void Configure(EntityTypeBuilder<Album> builder)
    {
        builder.HasKey(album => album.Id);
    }
}
