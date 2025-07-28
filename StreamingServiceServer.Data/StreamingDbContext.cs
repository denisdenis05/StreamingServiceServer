using System.Reflection;
using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data;

public class StreamingDbContext : DbContext
{
    public StreamingDbContext(DbContextOptions<StreamingDbContext> options) : base(options)
    {
    }

    public virtual DbSet<Album> Albums { get; set; }
    
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
