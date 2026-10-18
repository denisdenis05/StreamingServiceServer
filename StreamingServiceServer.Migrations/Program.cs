using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;

var verifyMigrations = args is ["--verify-migrations"];
if (args.Length > 0 && !verifyMigrations)
{
    Console.Error.WriteLine("Supported argument: --verify-migrations");
    return 1;
}

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
if (!verifyMigrations && string.IsNullOrWhiteSpace(connectionString))
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
    if (verifyMigrations)
    {
        var migrationCount = database.Database.GetMigrations().Count();
        if (migrationCount == 0)
        {
            Console.Error.WriteLine("No Data migrations were found in the published application.");
            return 1;
        }

        Console.WriteLine($"Loaded {migrationCount} Data migrations without connecting to a database.");
        return 0;
    }

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
