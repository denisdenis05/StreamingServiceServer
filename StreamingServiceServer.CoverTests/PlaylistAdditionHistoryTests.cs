using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SocialMedia.Data.Models;
using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class PlaylistAdditionHistoryTests
{
    private const string PreviousMigration = "20261120194359_AddLyricsLookupVersion";
    private const int FirstSavedPosition = 0;
    private const string PlaylistName = "Ordering regression";
    private const string Credential = "unused-ordering-fixture";
    private const string EmailSuffix = "@ordering.invalid";
    private const string FirstTitle = "First imported song";
    private const string SecondTitle = "Second imported song";
    private const string ThirdTitle = "Third imported song";
    private const string NewTitle = "Newly liked song";
    private const int FirstPage = 1;
    private const int SecondPage = 2;
    private const int PageSize = 2;
    private const int CompletePageSize = 10;
    private const int FirstDisplayPosition = 1;
    private const int SecondDisplayPosition = 2;
    private const int ThirdDisplayPosition = 3;
    private static readonly DateTimeOffset EarlierDate = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
    private static readonly DateTimeOffset LaterDate = DateTimeOffset.Parse("2020-02-01T00:00:00Z");
    private static readonly DateTimeOffset CurrentDate = DateTimeOffset.Parse("2026-11-21T12:00:00Z");

    [PostgresFact]
    public async Task UndatedImportKeepsBatchOrderThroughSelectionRepeatedSelectionAndClearing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var playlist = await AddPlaylist(database);
        var service = Service(database);
        await service.ImportPlaylistFromJsonAsync(playlist.OwnerId, playlist.Id, [Track(FirstTitle), Track(SecondTitle)], CancellationToken.None);
        await service.ImportPlaylistFromJsonAsync(playlist.OwnerId, playlist.Id, [Track(ThirdTitle)], CancellationToken.None);
        var savedOrders = await database.PlaylistRecordings.OrderBy(recording => recording.Order).Select(recording => recording.Order).ToArrayAsync();

        await AssertTitles(service, playlist, FirstTitle, SecondTitle, ThirdTitle);
        await service.SetLikedSongsPlaylistAsync(playlist.OwnerId, playlist.Id, CancellationToken.None);
        await AssertTitles(service, playlist, FirstTitle, SecondTitle, ThirdTitle);
        await service.SetLikedSongsPlaylistAsync(playlist.OwnerId, playlist.Id, CancellationToken.None);
        await AssertTitles(service, playlist, FirstTitle, SecondTitle, ThirdTitle);
        await service.SetLikedSongsPlaylistAsync(playlist.OwnerId, null, CancellationToken.None);
        await AssertTitles(service, playlist, FirstTitle, SecondTitle, ThirdTitle);
        Assert.Equal(savedOrders, await database.PlaylistRecordings.OrderBy(recording => recording.Order).Select(recording => recording.Order).ToArrayAsync());
        Assert.All(await database.PlaylistRecordings.ToListAsync(), recording => Assert.Null(recording.KnownAddedAt));
    }

    [PostgresFact]
    public async Task DatedImportDisplaysNewestFirstWithContinuousPositionsAcrossPages()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var playlist = await AddPlaylist(database);
        var service = Service(database);
        await service.ImportPlaylistFromJsonAsync(playlist.OwnerId, playlist.Id,
            [Track(FirstTitle, EarlierDate), Track(SecondTitle, LaterDate), Track(ThirdTitle)], CancellationToken.None);
        await AssertTitles(service, playlist, FirstTitle, SecondTitle, ThirdTitle);
        await service.SetLikedSongsPlaylistAsync(playlist.OwnerId, playlist.Id, CancellationToken.None);
        database.ChangeTracker.Clear();

        var firstPage = await service.GetPlaylistRecordingsAsync(playlist.Id, playlist.OwnerId, FirstPage, PageSize, CancellationToken.None);
        var secondPage = await service.GetPlaylistRecordingsAsync(playlist.Id, playlist.OwnerId, SecondPage, PageSize, CancellationToken.None);
        Assert.Equal([SecondTitle, FirstTitle], firstPage.Select(recording => recording.Title));
        Assert.Equal([FirstDisplayPosition, SecondDisplayPosition], firstPage.Select(recording => recording.PositionInAlbum));
        Assert.Equal(ThirdTitle, Assert.Single(secondPage).Title);
        Assert.Equal(ThirdDisplayPosition, Assert.Single(secondPage).PositionInAlbum);
        Assert.All(firstPage.Concat(secondPage), recording => Assert.True(recording.IsLiked));
        Assert.Equal(LaterDate.UtcDateTime, (await database.PlaylistRecordings.SingleAsync(recording => recording.Title == SecondTitle)).KnownAddedAt);
    }

    [PostgresFact]
    public async Task NewLikesAndReaddedSongsAppearFirstAndClearingRestoresSavedOrder()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var playlist = await AddPlaylist(database);
        var service = Service(database);
        await service.ImportPlaylistFromJsonAsync(playlist.OwnerId, playlist.Id, [Track(FirstTitle), Track(SecondTitle)], CancellationToken.None);
        await service.SetLikedSongsPlaylistAsync(playlist.OwnerId, playlist.Id, CancellationToken.None);
        var newIdentifier = Guid.NewGuid();
        await service.ToggleLikeAsync(playlist.OwnerId, newIdentifier, new AddSongToPlaylistRequest { Title = NewTitle }, CancellationToken.None);
        await AssertTitles(service, playlist, NewTitle, FirstTitle, SecondTitle);
        Assert.Equal(CurrentDate.UtcDateTime, (await database.PlaylistRecordings.SingleAsync(recording => recording.RecordingId == newIdentifier)).KnownAddedAt);
        var firstIdentifier = (await database.PlaylistRecordings.SingleAsync(recording => recording.Title == FirstTitle)).RecordingId;
        await service.ToggleLikeAsync(playlist.OwnerId, firstIdentifier, new AddSongToPlaylistRequest(), CancellationToken.None);
        await AssertTitles(service, playlist, NewTitle, SecondTitle);
        await service.ToggleLikeAsync(playlist.OwnerId, firstIdentifier, new AddSongToPlaylistRequest { Title = FirstTitle }, CancellationToken.None);
        await AssertTitles(service, playlist, FirstTitle, NewTitle, SecondTitle);
        await service.SetLikedSongsPlaylistAsync(playlist.OwnerId, null, CancellationToken.None);
        await AssertTitles(service, playlist, SecondTitle, NewTitle, FirstTitle);
    }

    [PostgresFact]
    public async Task ReplacingAnImportedRecordingPreservesItsAdditionHistoryAndPosition()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var playlist = await AddPlaylist(database);
        var service = Service(database);
        await service.ImportPlaylistFromJsonAsync(playlist.OwnerId, playlist.Id,
            [Track(FirstTitle, EarlierDate), Track(SecondTitle, LaterDate)], CancellationToken.None);
        await service.SetLikedSongsPlaylistAsync(playlist.OwnerId, playlist.Id, CancellationToken.None);
        var original = await database.PlaylistRecordings.SingleAsync(recording => recording.Title == FirstTitle);
        await database.Entry(original).ReloadAsync();
        var replacementIdentifier = Guid.NewGuid();
        await service.ReplacePlaylistRecordingAsync(playlist.OwnerId, playlist.Id, original.RecordingId,
            new AddSongToPlaylistRequest { Id = replacementIdentifier, Title = NewTitle }, CancellationToken.None);
        database.ChangeTracker.Clear();
        var replacement = await database.PlaylistRecordings.SingleAsync(recording => recording.RecordingId == replacementIdentifier);
        Assert.Equal(original.Order, replacement.Order);
        Assert.Equal(original.AddedAt, replacement.AddedAt);
        Assert.Equal(original.KnownAddedAt, replacement.KnownAddedAt);
        await AssertTitles(service, playlist, SecondTitle, NewTitle);
    }

    [PostgresFact]
    public async Task EveryNativeAdditionPathRecordsKnownAdditionTime()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var initialPlaylist = await AddPlaylist(database);
        var recording = new Recording { Id = Guid.NewGuid(), Title = FirstTitle, Cover = string.Empty,
            Release = new Release { Id = Guid.NewGuid(), Title = PlaylistName, Cover = string.Empty,
                Artist = new Artist { Id = Guid.NewGuid(), Name = PlaylistName, SortName = PlaylistName } } };
        database.Recordings.Add(recording);
        await database.SaveChangesAsync();
        var service = Service(database);
        var created = await service.CreatePlaylistAsync(initialPlaylist.OwnerId,
            new CreatePlaylistRequest { Name = PlaylistName, RecordingIds = [recording.Id] }, CancellationToken.None);
        await service.AddRecordingsToPlaylistAsync(initialPlaylist.OwnerId, initialPlaylist.Id,
            [new AddSongToPlaylistRequest { Id = Guid.NewGuid(), Title = NewTitle }], CancellationToken.None);
        await service.AddRecordingsToPlaylistFuzzyAsync(initialPlaylist.OwnerId, initialPlaylist.Id, [Track(FirstTitle)], CancellationToken.None);
        Assert.All(await database.PlaylistRecordings.ToListAsync(), entry => Assert.Equal(CurrentDate.UtcDateTime, entry.KnownAddedAt));
        Assert.Single(created.PlaylistRecordings);
    }

    [PostgresFact]
    public async Task MigrationLeavesLegacyAdditionHistoryUnknownAndRowsReadable()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: true);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        var playlist = await AddPlaylist(database);
        var recordingIdentifier = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PlaylistRecordings" ("PlaylistId", "RecordingId", "Title", "Artist", "Album", "Cover", "Order", "AddedById")
            VALUES ({playlist.Id}, {recordingIdentifier}, {FirstTitle}, {string.Empty}, {string.Empty}, {string.Empty}, {FirstSavedPosition}, {playlist.OwnerId})
            """);
        await migrator.MigrateAsync();
        await Service(database).SetLikedSongsPlaylistAsync(playlist.OwnerId, playlist.Id, CancellationToken.None);
        await AssertTitles(Service(database), playlist, FirstTitle);
        Assert.Null((await database.PlaylistRecordings.SingleAsync()).KnownAddedAt);
    }

    private static AddRecordingToPlaylistFuzzyRequest Track(string title, DateTimeOffset? addedAt = null) => new() { Name = title, AddedAt = addedAt };

    private static async Task<Playlist> AddPlaylist(StreamingDbContext database)
    {
        var owner = new User { Id = Guid.NewGuid(), Username = Guid.NewGuid().ToString(), Email = Guid.NewGuid() + EmailSuffix,
            PasswordHash = Credential, ProviderId = Credential };
        var playlist = new Playlist { Id = Guid.NewGuid(), Owner = owner, Name = PlaylistName };
        database.Playlists.Add(playlist);
        await database.SaveChangesAsync();
        return playlist;
    }

    private static async Task AssertTitles(LibraryService service, Playlist playlist, params string[] titles)
    {
        var recordings = await service.GetPlaylistRecordingsAsync(playlist.Id, playlist.OwnerId, FirstPage, CompletePageSize, CancellationToken.None);
        Assert.Equal(titles, recordings.Select(recording => recording.Title));
    }

    private static LibraryService Service(StreamingDbContext database) => new(database,
        DispatchProxy.Create<IExternalMusicSearchService, UnusedSearchProvider>(), timeProvider: new OrderingClock());

    private sealed class OrderingClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => CurrentDate;
    }

    public class UnusedSearchProvider : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => throw new NotSupportedException();
    }
}
