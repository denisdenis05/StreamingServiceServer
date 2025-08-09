using System.Reflection;
using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data;

public class StreamingDbContext : DbContext
{
    public StreamingDbContext(DbContextOptions<StreamingDbContext> options) : base(options)
    {
    }

    public DbSet<Artist> Artists { get; set; }
    public DbSet<Recording> Recordings { get; set; }
    public DbSet<ArtistAlias> ArtistAliases { get; set; }
    public DbSet<ArtistTag> ArtistTags { get; set; }
    public DbSet<RecordingArtistCredit> RecordingArtistCredits { get; set; }
    public DbSet<Release> Releases { get; set; }
    
    public DbSet<ReleaseToDownload> ReleasesToDownload { get; set; }
    public DbSet<PendingDownload> PendingDownloads { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ApplyConfigurationsFromAssembly(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
    
    private void ApplyConfigurationsFromAssembly(ModelBuilder modelBuilder)
    {
        var assembly = Assembly.GetExecutingAssembly();
        
        var entityTypeConfigurations = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>)))
            .ToList();

        foreach (var config in entityTypeConfigurations)
        {
            dynamic instance = Activator.CreateInstance(config);
            modelBuilder.ApplyConfiguration(instance);
        }
    }

}
