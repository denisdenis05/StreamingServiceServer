using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using StreamingServiceServer.Background;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;
namespace StreamingServiceServer.CoverTests;

[Trait("Category", "Postgres")]
public sealed class CoverJobIntegrationTests
{
    [PostgresFact]
    public async Task MetadataAndCoverJobArePersistedTogetherAndProcessedWithoutASecondScan()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var identifier = await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope()) {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var job = await database.CoverAssetJobs.SingleAsync(); Assert.Null(job.CompletedAt);
            Assert.Equal(identifier, job.EntityIdentifier);
        }
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using (var scope = fixture.Services.CreateScope()) {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var release = await database.Releases.SingleAsync(); var job = await database.CoverAssetJobs.SingleAsync();
            Assert.NotNull(job.CompletedAt); Assert.Contains("-v1-full.jpg", release.LocalCover);
            Assert.Contains("-v1-small.jpg", release.LocalSmallCover); Assert.Contains("-v1-thumbnail.jpg", release.LocalVerySmallCover);
            Assert.Equal(1, fixture.Assets.Handler.Requests);
        }
    }
    [PostgresFact]
    public async Task FailedJobRetainsItsRetryAcrossWorkerRecreation()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        fixture.Assets.Renderer.Fail = true; await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using (var scope = fixture.Services.CreateScope()) {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>(); var job = await database.CoverAssetJobs.SingleAsync();
            Assert.Equal(1, job.Attempts); Assert.Null(job.CompletedAt); Assert.Null(job.LeaseToken); Assert.NotEmpty(job.LastFailure);
            Assert.True(job.NextAttemptAt > fixture.Clock.GetUtcNow());
        }
        fixture.Assets.Renderer.Fail = false; fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.NewWorker().ProcessDueAsync(CancellationToken.None);
        using var verification = fixture.Services.CreateScope();
        Assert.NotNull((await verification.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.SingleAsync()).CompletedAt);
    }
    [PostgresFact]
    public async Task RepairRedownloadsMissingFilesEvenWhenLocalCoverIsPopulated()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        foreach (var path in Directory.GetFiles(fixture.Assets.Directory, "*-v1-small.jpg")) File.Delete(path);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.Worker.RepairAsync(CancellationToken.None); await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var release = await database.Releases.SingleAsync();
        Assert.True(File.Exists(Path.Combine(fixture.Assets.Directory, new Uri(release.LocalSmallCover).Segments.Last())));
        Assert.Equal(1, fixture.Assets.Handler.Requests);
    }
    [PostgresFact]
    public async Task ChangedCoverReplacesOldCompletedJobAndPublishesNewVersion()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync(); await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        string oldCover;
        using (var scope = fixture.Services.CreateScope()) {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>(); var release = await database.Releases.SingleAsync();
            oldCover = release.LocalCover; release.Cover = "https://new.invalid/cover.jpg"; await database.SaveChangesAsync();
            Assert.Null((await database.CoverAssetJobs.SingleAsync()).CompletedAt);
        }
        fixture.Assets.Handler.Content = AssetFixture.Image.Concat(new byte[] { 9 }).ToArray();
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using var verification = fixture.Services.CreateScope(); var current = await verification.ServiceProvider.GetRequiredService<StreamingDbContext>().Releases.SingleAsync();
        Assert.NotEqual(oldCover, current.LocalCover);
    }
    [PostgresFact]
    public async Task ConcurrentWorkersClaimOnePersistedJob()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        await Task.WhenAll(fixture.NewWorker().ProcessDueAsync(CancellationToken.None), fixture.NewWorker().ProcessDueAsync(CancellationToken.None));
        Assert.Equal(1, fixture.Assets.Handler.Requests);
    }
    [PostgresFact]
    public async Task SourceChangedDuringDownloadCannotBeOverwrittenByOldJob()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Assets.Handler.Wait = async () => { started.TrySetResult(); await finish.Task; };
        var processing = fixture.Worker.ProcessDueAsync(CancellationToken.None); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var scope = fixture.Services.CreateScope()) {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>(); var release = await database.Releases.SingleAsync();
            release.Cover = "https://changed.invalid/cover.jpg"; release.LocalCover = "newer-local"; await database.SaveChangesAsync();
        }
        finish.SetResult(); await processing;
        using var verification = fixture.Services.CreateScope(); var current = await verification.ServiceProvider.GetRequiredService<StreamingDbContext>().Releases.SingleAsync();
        Assert.Equal("newer-local", current.LocalCover);
        Assert.Null((await verification.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.SingleAsync()).CompletedAt);
    }
    [PostgresFact]
    public async Task ExistingMetadataWithoutJobsIsBackfilled()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        using (var scope = fixture.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.ExecuteDeleteAsync();
        await fixture.Worker.RepairAsync(CancellationToken.None);
        using var verification = fixture.Services.CreateScope();
        Assert.Single(await verification.ServiceProvider.GetRequiredService<StreamingDbContext>().CoverAssetJobs.ToListAsync());
    }
    [PostgresFact]
    public async Task LibraryAndMetadataResponsesPreferTheDownloadedVariants()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); var identifier = await fixture.AddReleaseAsync();
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var user = new SocialMedia.Data.Models.User { Id = Guid.NewGuid(), Username = "cover-test", Email = "cover-test@example.invalid",
            PasswordHash = "fixture-hash", ProviderId = "fixture-provider" };
        database.Users.Add(user);
        database.UserSavedAlbums.Add(new StreamingServiceServer.Data.Models.Library.UserSavedAlbum { UserId = user.Id, ReleaseId = identifier });
        await database.SaveChangesAsync();
        var library = new StreamingServiceServer.Business.Services.MusicSearch.LibraryService(database, null!);
        var response = Assert.Single(await library.GetLibraryAsync(user.Id, CancellationToken.None));
        var release = await database.Releases.Include(current => current.Artist).SingleAsync();
        var metadata = StreamingServiceServer.Business.Models.MusicSearch.MusicBrainzMapper.ToResponse(release, null!);
        Assert.Equal(release.LocalCover, response.Cover); Assert.Equal(release.LocalSmallCover, response.SmallCover);
        Assert.Equal(release.LocalVerySmallCover, response.VerySmallCover); Assert.Equal(metadata.Cover, response.Cover);
    }
    [PostgresFact]
    public async Task MetadataFailureRollsBackTheEnqueuedJob()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        database.Releases.Add(new Release { Id = Guid.NewGuid(), Cover = AssetFixture.Source, Artist = null! });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync()); database.ChangeTracker.Clear();
        Assert.Empty(await database.CoverAssetJobs.ToListAsync()); Assert.Empty(await database.Releases.ToListAsync());
    }
    [PostgresFact]
    public async Task LegacyUploadedCoversMoveToTheCurrentOriginWithoutContactingTheOldHost()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); await fixture.AddReleaseAsync();
        Directory.CreateDirectory(fixture.Assets.Directory);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Assets.Directory, "legacy.jpg"), AssetFixture.Image);
        using (var scope = fixture.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>(); var release = await database.Releases.SingleAsync();
            release.Cover = release.LocalCover = "https://old.invalid/images/legacy.jpg"; await database.SaveChangesAsync();
        }
        fixture.Assets.Options.Domain = "https://new.invalid";
        await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        using (var scope = fixture.Services.CreateScope())
        {
            var release = await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().Releases.SingleAsync();
            Assert.StartsWith(fixture.Assets.Options.Domain, release.LocalCover); Assert.Equal(release.Cover, release.LocalCover);
        }
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.Worker.RepairAsync(CancellationToken.None); await fixture.Worker.ProcessDueAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Assets.Handler.Requests);
    }
    [PostgresFact]
    public async Task CoverJobMigrationCreatesTheDurableQueueOnAFreshDatabase()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(true);
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        Assert.Empty(await database.CoverAssetJobs.ToListAsync());
        Assert.Contains("20261016190024_AddCoverAssetJobs", await database.Database.GetAppliedMigrationsAsync());
    }
}
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset current = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan duration) => current += duration;
}
internal sealed class DatabaseFixture : IAsyncDisposable
{
    public ServiceProvider Services { get; }
    public AssetFixture Assets { get; } = new();
    public TestClock Clock { get; } = new();
    private readonly NpgsqlConnectionStringBuilder master;
    private readonly string databaseName;
    public CoverAssetJobService Worker => NewWorker();
    private DatabaseFixture(NpgsqlConnectionStringBuilder master, string name)
    {
        this.master = master; databaseName = name;
        var connection = new NpgsqlConnectionStringBuilder(master.ConnectionString) { Database = name };
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock); services.AddScoped<CoverJobInterceptor>();
        services.AddDbContext<StreamingDbContext>((provider, options) => options.UseNpgsql(connection.ConnectionString)
            .AddInterceptors(provider.GetRequiredService<CoverJobInterceptor>()));
        Services = services.BuildServiceProvider();
    }
    public CoverAssetJobService NewWorker() => new(Services.GetRequiredService<IServiceScopeFactory>(), Assets.Store,
        Options.Create(Assets.Options), Clock, NullLogger<CoverAssetJobService>.Instance);
    public static async Task<DatabaseFixture> CreateAsync(bool migrate = false)
    {
        var configured = Environment.GetEnvironmentVariable("COVER_TEST_POSTGRES") ?? throw new InvalidOperationException("Set COVER_TEST_POSTGRES to the disposable local Unix socket database.");
        var master = new NpgsqlConnectionStringBuilder(configured);
        if (master.Host is null || (!master.Host.StartsWith("/tmp/", StringComparison.Ordinal) && master.Host != "/tmp")) throw new InvalidOperationException("Integration tests only accept a /tmp Unix socket.");
        var name = "cover_test_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(master.ConnectionString)) {
            await connection.OpenAsync(); await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection); await command.ExecuteNonQueryAsync();
        }
        var fixture = new DatabaseFixture(master, name);
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        if (migrate) await database.Database.MigrateAsync(); else await database.Database.EnsureCreatedAsync();
        return fixture;
    }
    public async Task<Guid> AddReleaseAsync()
    {
        using var scope = Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var release = new Release { Id = Guid.NewGuid(), Title = "test release", Cover = AssetFixture.Source,
            Artist = new Artist { Id = Guid.NewGuid(), Name = "test artist", SortName = "test artist" } };
        database.Releases.Add(release); await database.SaveChangesAsync(); return release.Id;
    }
    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync(); Assets.Dispose(); NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(master.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", connection); await command.ExecuteNonQueryAsync();
    }
}

internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COVER_TEST_POSTGRES")))
            Skip = "Set COVER_TEST_POSTGRES to run against the disposable local PostgreSQL instance.";
    }
}
