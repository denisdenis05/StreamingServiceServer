using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.Services.SpotifyCatalog;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class SpotifyRecordingCatalogTests
{
    [Fact]
    public void ProjectionPreservesAllRecordingCreditsAndFallsBackToArtistNames()
    {
        var recording = Recording();
        recording.ArtistCredit = [
            new RecordingArtistCredit { Id = Guid.Parse(Fixture.FirstCreditIdentifier), Name = Fixture.CreditedArtist },
            new RecordingArtistCredit { Id = Guid.Parse(Fixture.SecondCreditIdentifier), Name = Fixture.Whitespace,
                Artist = new Artist { Id = Guid.Parse(Fixture.OtherArtistIdentifier), Name = Fixture.OtherArtist } }
        ];
        var projected = RecordingCatalogQuery.Project(new[] { recording }.AsQueryable()).Single();
        var result = RecordingCatalogQuery.Map(projected);
        Assert.Equal(Fixture.RecordingIdentifier, result.Identifier);
        Assert.Equal(new[] { Fixture.CreditedArtist, Fixture.OtherArtist }, result.ArtistNames);
        Assert.Equal(RecordingCatalogPolicy.RecordingCreditSource, result.ArtistCreditSource);
        Assert.Equal(Fixture.DurationMilliseconds, result.DurationMilliseconds);
    }

    [Fact]
    public void ProjectionMarksReleaseArtistFallbackInsteadOfClaimingRecordingCredits()
    {
        var projected = RecordingCatalogQuery.Project(new[] { Recording() }.AsQueryable()).Single();
        var result = RecordingCatalogQuery.Map(projected);
        Assert.Equal(Fixture.ReleaseArtist, result.Author);
        Assert.Equal(RecordingCatalogPolicy.ReleaseCreditSource, result.ArtistCreditSource);
    }

    [Fact]
    public void ProjectionRemovesDuplicateCreditsWithoutReturningMutableLists()
    {
        var projected = new RecordingCatalogProjection { Identifier = Guid.Parse(Fixture.RecordingIdentifier),
            Title = Fixture.Title, ArtistNames = [Fixture.CreditedArtist, Fixture.CreditedArtist] };
        var result = RecordingCatalogQuery.Map(projected);
        projected.ArtistNames.Clear();
        Assert.Single(result.ArtistNames);
        Assert.Equal(Fixture.CreditedArtist, result.Author);
    }

    [Fact]
    public void ProjectionOmitsInvalidDurationInsteadOfInventingAValue()
    {
        var projected = new RecordingCatalogProjection { DurationMilliseconds = Fixture.InvalidDurationMilliseconds };
        Assert.Null(RecordingCatalogQuery.Map(projected).DurationMilliseconds);
    }

    [Fact]
    public async Task InvalidSearchAndResultLimitsFailBeforeDatabaseAccess()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        var query = new RecordingCatalogQuery(database);
        await Assert.ThrowsAsync<ArgumentException>(() => query.Search(Fixture.EmptyText, Fixture.CreditedArtist,
            RecordingCatalogPolicy.DefaultLimit, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => query.Search(Fixture.Title, Fixture.CreditedArtist,
            RecordingCatalogPolicy.MaximumLimit + Fixture.LimitOverflow, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => query.Lookup(Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsLookupAndSearchBeforeDatabaseAccess()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var query = new RecordingCatalogQuery(database);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.Lookup(Guid.Parse(Fixture.RecordingIdentifier), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.Search(Fixture.Title, Fixture.CreditedArtist,
            RecordingCatalogPolicy.DefaultLimit, cancellation.Token));
    }

    [Fact]
    public void PostgreSqlProjectionTranslatesBoundedSearchWithoutLoadingCovers()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>()
            .UseNpgsql(Fixture.TranslationOnlyConnection).Options);
        var candidates = RecordingCatalogQuery.Candidates(database.Recordings.AsNoTracking(), Fixture.Title,
            Fixture.MultipleArtists, Fixture.SingleResultLimit);
        var statement = RecordingCatalogQuery.Project(candidates).ToQueryString();
        Assert.Contains(Fixture.SqlLimit, statement);
        Assert.Contains(Fixture.SqlSimilarity, statement);
        Assert.DoesNotContain(Fixture.CoverColumn, statement);
    }

    [Fact]
    public void EndpointRequiresExistingApplicationBearerRoles()
    {
        var authorization = Assert.Single(typeof(SpotifyRecordingCatalogController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>());
        Assert.Equal(SpotifyCatalogRoutes.AuthenticationScheme, authorization.AuthenticationSchemes);
        Assert.Equal(SpotifyCatalogRoutes.AllowedRoles, authorization.Roles);
    }

    [Fact]
    public async Task ControllerReturnsNotFoundForUnknownRecording()
    {
        var controller = new SpotifyRecordingCatalogController(new EmptyCatalog());
        Assert.IsType<NotFoundResult>(await controller.Lookup(Guid.Parse(Fixture.RecordingIdentifier), CancellationToken.None));
    }

    [Fact]
    public async Task ControllerReturnsBadRequestForInvalidSearch()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        var controller = new SpotifyRecordingCatalogController(new RecordingCatalogQuery(database));
        Assert.IsType<BadRequestObjectResult>(await controller.Search(Fixture.EmptyText, Fixture.CreditedArtist, CancellationToken.None));
    }

    [PostgresFact]
    [Trait(Fixture.CategoryName, Fixture.PostgresCategory)]
    public async Task PostgreSqlSearchPreservesMultipleArtistsAndBoundsReturnedRowsWithoutTracking()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var first = Recording();
        first.ArtistCredit = [new RecordingArtistCredit { Id = Guid.Parse(Fixture.FirstCreditIdentifier), Name = Fixture.CreditedArtist },
            new RecordingArtistCredit { Id = Guid.Parse(Fixture.SecondCreditIdentifier), Name = Fixture.OtherArtist }];
        var second = Recording();
        second.Id = Guid.Parse(Fixture.OtherRecordingIdentifier);
        second.Release = first.Release;
        database.Recordings.AddRange(first, second);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var result = await new RecordingCatalogQuery(database).Search(Fixture.Title, Fixture.MultipleArtists, Fixture.SingleResultLimit, CancellationToken.None);
        Assert.Single(result);
        Assert.Equal(Fixture.RecordingIdentifier, result.Single().Identifier);
        Assert.Equal(new[] { Fixture.CreditedArtist, Fixture.OtherArtist }, result.Single().ArtistNames);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [PostgresFact]
    [Trait(Fixture.CategoryName, Fixture.PostgresCategory)]
    public async Task PostgreSqlLookupReturnsOnlyTheRequestedRecordingAndReadOnlyMetadata()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        database.Recordings.Add(Recording()); await database.SaveChangesAsync(); database.ChangeTracker.Clear();
        var query = new RecordingCatalogQuery(database);
        var result = await query.Lookup(Guid.Parse(Fixture.RecordingIdentifier), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(Fixture.Title, result.Title);
        Assert.Equal(Fixture.ReleaseArtist, result.Author);
        Assert.Null(await query.Lookup(Guid.Parse(Fixture.OtherRecordingIdentifier), CancellationToken.None));
        Assert.Empty(database.ChangeTracker.Entries());
    }

    private static Recording Recording() => new()
    {
        Id = Guid.Parse(Fixture.RecordingIdentifier), Title = Fixture.Title,
        Length = Fixture.DurationMilliseconds, Cover = Fixture.EmptyText,
        Release = new Release { Id = Guid.Parse(Fixture.ReleaseIdentifier), Title = Fixture.ReleaseTitle, Cover = Fixture.EmptyText,
            Artist = new Artist { Id = Guid.Parse(Fixture.ArtistIdentifier), Name = Fixture.ReleaseArtist } }
    };

    private sealed class EmptyCatalog : IRecordingCatalogQuery
    {
        public Task<RecordingCatalogEntry?> Lookup(Guid identifier, CancellationToken cancellationToken) => Task.FromResult<RecordingCatalogEntry?>(null);
        public Task<IReadOnlyList<RecordingCatalogEntry>> Search(string title, string author, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RecordingCatalogEntry>>([]);
    }

    private static class Fixture
    {
        public const string RecordingIdentifier = "11111111-1111-4111-8111-111111111111";
        public const string OtherRecordingIdentifier = "22222222-2222-4222-8222-222222222222";
        public const string ArtistIdentifier = "33333333-3333-4333-8333-333333333333";
        public const string OtherArtistIdentifier = "44444444-4444-4444-8444-444444444444";
        public const string ReleaseIdentifier = "55555555-5555-4555-8555-555555555555";
        public const string FirstCreditIdentifier = "66666666-6666-4666-8666-666666666666";
        public const string SecondCreditIdentifier = "77777777-7777-4777-8777-777777777777";
        public const string Title = "Catalog Test Song";
        public const string CreditedArtist = "Credited Artist";
        public const string OtherArtist = "Other Artist";
        public const string MultipleArtists = "Credited Artist, Other Artist";
        public const string ReleaseArtist = "Release Artist";
        public const string ReleaseTitle = "Catalog Test Album";
        public const string EmptyText = "";
        public const string Whitespace = "  ";
        public const int SingleResultLimit = 1;
        public const int LimitOverflow = 1;
        public const int DurationMilliseconds = 240000;
        public const int InvalidDurationMilliseconds = -1;
        public const string TranslationOnlyConnection = "Host=localhost;Database=translation_only";
        public const string SqlLimit = "LIMIT";
        public const string SqlSimilarity = "similarity";
        public const string CoverColumn = "\"Cover\"";
        public const string CategoryName = "Category";
        public const string PostgresCategory = "Postgres";
    }
}
