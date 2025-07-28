namespace StreamingServiceServer.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;


public class StreamingDbContextFactory : IDesignTimeDbContextFactory<StreamingDbContext>
{
    public StreamingDbContext CreateDbContext(string[] args)
    {
        var configPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../StreamingServiceServer.API"));

        var configuration = new ConfigurationBuilder()
            .SetBasePath(configPath)
            .AddJsonFile("appsettings.json")
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<StreamingDbContext>();
        optionsBuilder.UseNpgsql(configuration.GetConnectionString("DefaultConnection"));

        return new StreamingDbContext(optionsBuilder.Options);
    }
}
