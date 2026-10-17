using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.Services.Images.Progress;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;
namespace StreamingServiceServer.CoverTests;
public sealed class CoverDownloadProgressTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(4, 1, 25)]
    [InlineData(3, 1, 33.3)]
    public void SummaryReportsCompletedAlbumsWithoutDividingByZero(int total, int downloaded, double expected) =>
        Assert.Equal(expected, new CoverProgressSummary(total, downloaded, 0, 0).Percentage);
    [Theory]
    [InlineData(true, true, true, "downloaded", 100)]
    [InlineData(true, false, false, "partial", 33.3)]
    [InlineData(false, true, true, "partial", 66.7)]
    [InlineData(false, false, false, "queued", 0)]
    public void EachAlbumReportsItsOwnLocalVariantProgress(bool full, bool small, bool thumbnail, string status, double expected)
    {
        var result = new CoverProgressRow { FullReady = full, SmallReady = small, ThumbnailReady = thumbnail, HasSource = true }.ToAlbum(DateTimeOffset.UtcNow);
        Assert.Equal(status, result.Status); Assert.Equal(expected, result.Percentage);
    }
    [Fact]
    public void CoverStatesDistinguishUnavailableActiveRetryAndMissingSources()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("unavailable", new CoverProgressRow { HasSource = true, Suspended = true }.ToAlbum(now).Status);
        Assert.Equal("downloading", new CoverProgressRow { HasSource = true, LeaseUntil = now.AddMinutes(1) }.ToAlbum(now).Status);
        Assert.Equal("retrying", new CoverProgressRow { HasSource = true, Attempts = 1, NextAttemptAt = now.AddMinutes(1) }.ToAlbum(now).Status);
        Assert.Equal("no-source", new CoverProgressRow().ToAlbum(now).Status);
    }
    [Fact]
    public void AlreadyDownloadedCoversRemainCompleteDuringARefreshFailure()
    {
        var album = new CoverProgressRow { FullReady = true, SmallReady = true, ThumbnailReady = true, HasSource = true, Suspended = true }.ToAlbum(DateTimeOffset.UtcNow);
        Assert.Equal("downloaded", album.Status); Assert.Equal(100, album.Percentage);
    }
    [Fact]
    public void CoverEndpointRequiresAdministratorAuthorization()
    {
        var attribute = Assert.Single(typeof(CoverProgressController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("Admin", attribute.Roles);
        Assert.Single(typeof(CoverProgressController).GetMethod(nameof(CoverProgressController.Get))!.GetCustomAttributes(typeof(HttpGetAttribute), true));
    }
    [Theory]
    [InlineData(0, 25, CoverProgressFilter.All)]
    [InlineData(1, 101, CoverProgressFilter.All)]
    [InlineData(1, 25, (CoverProgressFilter)99)]
    public async Task InvalidPagingAndFiltersFailBeforeAnyDatabaseQuery(int page, int pageSize, CoverProgressFilter filter)
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new CoverDownloadProgress(database, TimeProvider.System).ReadAsync(page, pageSize, filter, null, CancellationToken.None));
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ReadOnlyQueryIncludesCompletedLegacyPartialAndMissingAlbumsWithoutJoiningRecordingJobs()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var artist = new Artist { Id = Guid.NewGuid(), Name = "cover artist" };
        var completed = NewRelease("Alpha", artist, 3); var partial = NewRelease("Beta", artist, 1); var unavailable = NewRelease("Gamma", artist, 0);
        database.Releases.AddRange(completed, partial, unavailable); await database.SaveChangesAsync();
        var unavailableJob = await database.CoverAssetJobs.SingleAsync(job => job.EntityIdentifier == unavailable.Id); unavailableJob.Suspended = true;
        database.CoverAssetJobs.Add(new CoverAssetJob { EntityKind = CoverEntityKind.Recording, EntityIdentifier = completed.Id });
        await database.SaveChangesAsync(); database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync();
        await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var result = await new CoverDownloadProgress(database, fixture.Clock).ReadAsync(1, 25, CoverProgressFilter.All, null, CancellationToken.None);
        Assert.Equal(3, result.Summary.TotalAlbums); Assert.Equal(1, result.Summary.DownloadedAlbums);
        Assert.Equal(1, result.Summary.PartialAlbums); Assert.Equal(1, result.Summary.UnavailableAlbums); Assert.Equal(33.3, result.Summary.Percentage);
        Assert.Equal(new[] { "downloaded", "partial", "unavailable" }, result.Albums.Select(album => album.Status));
        Assert.Empty(database.ChangeTracker.Entries());
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task FilteringAndPagingKeepTheCatalogDenominatorAndClampPages()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Matched Artist" };
        database.Releases.AddRange(NewRelease("Alpha", artist, 3), NewRelease("Beta", artist, 0), NewRelease("Delta", artist, 0));
        await database.SaveChangesAsync(); database.ChangeTracker.Clear();
        var progress = new CoverDownloadProgress(database, fixture.Clock);
        var missing = await progress.ReadAsync(99, 1, CoverProgressFilter.Missing, "matched artist", CancellationToken.None);
        Assert.Equal(3, missing.Summary.TotalAlbums); Assert.Equal(2, missing.FilteredAlbums); Assert.Equal(2, missing.Page);
        Assert.Equal("Delta", Assert.Single(missing.Albums).Title);
        var downloaded = await progress.ReadAsync(1, 25, CoverProgressFilter.Downloaded, "ALPHA", CancellationToken.None);
        Assert.Equal(1, downloaded.FilteredAlbums); Assert.Equal(3, downloaded.Summary.TotalAlbums);
    }
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task EmptyCatalogProducesAnEmptyFirstPageWithZeroProgress()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); using var scope = fixture.Services.CreateScope();
        var result = await new CoverDownloadProgress(scope.ServiceProvider.GetRequiredService<StreamingDbContext>(), fixture.Clock)
            .ReadAsync(8, 25, CoverProgressFilter.All, null, CancellationToken.None);
        Assert.Equal(0, result.Summary.Percentage); Assert.Equal(1, result.Page); Assert.Equal(0, result.FilteredAlbums); Assert.Empty(result.Albums);
    }
    private static Release NewRelease(string title, Artist artist, int variants) => new() {
        Id = Guid.NewGuid(), Title = title, Artist = artist, Cover = AssetFixture.Source,
        LocalCover = variants > 0 ? "https://images.invalid/full.jpg" : string.Empty,
        LocalSmallCover = variants > 1 ? "https://images.invalid/small.jpg" : string.Empty,
        LocalVerySmallCover = variants > 2 ? "https://images.invalid/thumbnail.jpg" : string.Empty };
}
