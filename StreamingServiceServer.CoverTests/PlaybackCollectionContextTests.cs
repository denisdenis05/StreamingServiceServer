using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocialMedia.Data.Models;
using StreamingServiceServer.Business.Models.LastFm;
using StreamingServiceServer.Business.Services.LastFm;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class PlaybackCollectionContextTests
{
    private const string AccountValue = "playback-context-test";
    private const string AccountEmail = "playback-context@test.invalid";
    private const string TrackTitle = "Context test track";
    private const string AlbumTitle = "Context test album";
    private const string ArtistName = "Context test artist";
    private const int TrackDurationSeconds = 180;
    private const string SourceAlbumProperty = nameof(Listen.SourceAlbumId);
    private const int CollectionIndexCount = 2;
    private const string PreviousMigrationName = "20261018190000_IdentifyAudioImports";
    private const string CollectionMigrationName = "TrackPlaybackCollectionContext";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UnknownAndConflictingContextsRequireNoDatabaseAndNeverInferAlbum(bool hasAlbum, bool hasPlaylist)
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        var request = Request(Guid.NewGuid());
        request.SourceAlbumId = hasAlbum ? Guid.NewGuid() : null;
        request.PlaylistId = hasPlaylist ? Guid.NewGuid() : null;
        Assert.Equal(PlaybackCollectionContext.Unknown,
            await PlaybackCollectionContext.ResolveAsync(database, Guid.NewGuid(), request));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyCollectionIdentifiersAreDiscardedWithoutDatabaseAccess(bool useAlbum)
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        var request = Request(Guid.NewGuid());
        request.SourceAlbumId = useAlbum ? Guid.Empty : null;
        request.PlaylistId = useAlbum ? null : Guid.Empty;
        Assert.Equal(PlaybackCollectionContext.Unknown,
            await PlaybackCollectionContext.ResolveAsync(database, Guid.NewGuid(), request));
    }

    [Fact]
    public void MigrationAddsNullableAlbumContextWithoutBackfillingHistory()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>()
            .UseNpgsql("Host=/tmp;Database=unused").Options);
        var migrations = database.GetService<IMigrationsAssembly>();
        var migrationType = migrations.Migrations.Single(migration => migration.Key.EndsWith(CollectionMigrationName)).Value;
        var migration = migrations.CreateMigration(migrationType, database.Database.ProviderName!);
        var column = Assert.Single(migration.UpOperations.OfType<AddColumnOperation>());
        Assert.Equal(SourceAlbumProperty, column.Name);
        Assert.True(column.IsNullable);
        Assert.Null(column.DefaultValue);
        Assert.Null(column.DefaultValueSql);
        Assert.Empty(migration.UpOperations.OfType<SqlOperation>());
        Assert.Empty(migration.UpOperations.OfType<UpdateDataOperation>());
        Assert.Equal(CollectionIndexCount, migration.UpOperations.OfType<CreateIndexOperation>().Count());
    }

    [PostgresFact]
    public async Task AlbumPlaybackPersistsOnlyExplicitMatchingAlbumAndKeepsTrackMetadata()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var account = AddAccount(database);
        var recording = AddRecording(database);
        await database.SaveChangesAsync();
        var request = Request(recording.Id);
        request.SourceAlbumId = recording.Release.Id;
        var sessionIdentifier = await Service(database, fixture.Services).StartPlaybackSession(account.Id, request);
        database.ChangeTracker.Clear();
        var listen = await database.Listens.SingleAsync(item => item.SessionId == sessionIdentifier);
        Assert.Equal(recording.Release.Id, listen.SourceAlbumId);
        Assert.Null(listen.PlaylistId);
        Assert.Equal(recording.Id, listen.RecordingId);
        Assert.Equal(TrackTitle, listen.TrackName);
        Assert.Equal(AlbumTitle, listen.Album);
        Assert.Equal(TrackDurationSeconds, listen.TrackDurationSeconds);
    }

    [PostgresFact]
    public async Task OwnedPlaylistPlaybackPersistsPlaylistEvenWithoutCatalogRecording()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var account = AddAccount(database);
        var recordingIdentifier = Guid.NewGuid();
        var playlist = AddPlaylist(database, account.Id, recordingIdentifier);
        await database.SaveChangesAsync();
        var request = Request(recordingIdentifier);
        request.PlaylistId = playlist.Id;
        var sessionIdentifier = await Service(database, fixture.Services).StartPlaybackSession(account.Id, request);
        database.ChangeTracker.Clear();
        var listen = await database.Listens.SingleAsync(item => item.SessionId == sessionIdentifier);
        Assert.Equal(playlist.Id, listen.PlaylistId);
        Assert.Null(listen.SourceAlbumId);
        Assert.Equal(recordingIdentifier, listen.RecordingId);
    }

    [PostgresFact]
    public async Task InvalidForeignAndConflictingContextsPreserveTrackListenWithoutCollectionAttribution()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var account = AddAccount(database);
        var foreignAccount = AddAccount(database);
        var recording = AddRecording(database);
        var ownedPlaylist = AddPlaylist(database, account.Id, Guid.NewGuid());
        var foreignPlaylist = AddPlaylist(database, foreignAccount.Id, recording.Id);
        await database.SaveChangesAsync();
        var requests = new[]
        {
            Request(recording.Id),
            Request(recording.Id, Guid.NewGuid(), null),
            Request(recording.Id, null, ownedPlaylist.Id),
            Request(recording.Id, null, foreignPlaylist.Id),
            Request(recording.Id, null, Guid.NewGuid()),
            Request(recording.Id, recording.Release.Id, foreignPlaylist.Id)
        };
        foreach (var request in requests)
        {
            var sessionIdentifier = await Service(database, fixture.Services).StartPlaybackSession(account.Id, request);
            var listen = await database.Listens.SingleAsync(item => item.SessionId == sessionIdentifier);
            Assert.Null(listen.SourceAlbumId);
            Assert.Null(listen.PlaylistId);
            Assert.Equal(recording.Id, listen.RecordingId);
        }
    }

    [PostgresFact]
    public async Task ApplyingMigrationKeepsLegacyListenUnknownAndRetainsTrackHistory()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: true);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var account = AddAccount(database);
        await database.SaveChangesAsync();
        var recordingIdentifier = Guid.NewGuid();
        var sessionIdentifier = await Service(database, fixture.Services).StartPlaybackSession(account.Id, Request(recordingIdentifier));
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigrationName);
        await migrator.MigrateAsync();
        database.ChangeTracker.Clear();
        var listen = await database.Listens.SingleAsync(item => item.SessionId == sessionIdentifier);
        Assert.Null(listen.SourceAlbumId);
        Assert.Null(listen.PlaylistId);
        Assert.Equal(recordingIdentifier, listen.RecordingId);
        Assert.Equal(AlbumTitle, listen.Album);
    }

    private static StartPlaybackRequest Request(Guid recordingIdentifier, Guid? albumIdentifier = null, Guid? playlistIdentifier = null) => new()
    {
        TrackId = recordingIdentifier, Artist = ArtistName, Track = TrackTitle, Album = AlbumTitle,
        DurationSeconds = TrackDurationSeconds, SourceAlbumId = albumIdentifier, PlaylistId = playlistIdentifier
    };

    private static User AddAccount(StreamingDbContext database)
    {
        var account = new User { Id = Guid.NewGuid(), Username = Guid.NewGuid().ToString(),
            Email = Guid.NewGuid() + AccountEmail, PasswordHash = AccountValue, ProviderId = AccountValue };
        database.Users.Add(account);
        return account;
    }

    private static Recording AddRecording(StreamingDbContext database)
    {
        var recording = new Recording { Id = Guid.NewGuid(), Title = TrackTitle, Cover = string.Empty,
            Release = new Release { Id = Guid.NewGuid(), Title = AlbumTitle, Cover = string.Empty,
                Artist = new Artist { Id = Guid.NewGuid(), Name = ArtistName, SortName = ArtistName } } };
        database.Recordings.Add(recording);
        return recording;
    }

    private static Playlist AddPlaylist(StreamingDbContext database, Guid ownerIdentifier, Guid recordingIdentifier)
    {
        var playlist = new Playlist { Id = Guid.NewGuid(), OwnerId = ownerIdentifier, Name = AlbumTitle };
        playlist.PlaylistRecordings.Add(new PlaylistRecording { RecordingId = recordingIdentifier, AddedById = ownerIdentifier });
        database.Playlists.Add(playlist);
        return playlist;
    }

    private static LastFmService Service(StreamingDbContext database, ServiceProvider services) => new(
        new HttpClient(new RejectNetworkHandler()), new ConfigurationBuilder().Build(), database,
        services.GetRequiredService<IServiceScopeFactory>());

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("History tests must not contact Last.fm.");
    }
}
