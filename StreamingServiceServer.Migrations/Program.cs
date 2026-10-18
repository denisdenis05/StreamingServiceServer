using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set ConnectionStrings__DefaultConnection before applying migrations.");
    return 1;
}

try
{
    var databaseOptions = new DbContextOptionsBuilder<StreamingDbContext>()
        .UseNpgsql(connectionString)
        .Options;
    await using var database = new StreamingDbContext(databaseOptions);
    Console.WriteLine("Applying pending migrations from StreamingServiceServer.Data.");
    await database.Database.MigrateAsync();
    Console.WriteLine("Database migrations completed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Database migration failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
