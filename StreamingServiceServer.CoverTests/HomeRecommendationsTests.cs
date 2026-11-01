using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialMedia.Data.Models;
using StreamingServiceServer.Business.Constants;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.Recommendation;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class HomeRecommendationsTests
{
    private const string FixtureName = "Home collection";
    private const string FixtureEmailSuffix = "@home.test.invalid";
    private const string UnusedCredential = "unused";
    private const string FixtureCover = "https://covers.test.invalid/home";
    private const string UnknownSection = "missing-section";
    private const int NegativeQuickCount = -1;
    private const int RepeatedListenCount = 60;
    private const int LargeQuickCount = 1000;
    private const int FixtureAlbumCount = 15;
    private const int RecentMinuteOffset = 10;
    private static readonly DateTime FirstPlayedAt = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task UnknownAndFriendsSectionsDoNotQueryOrInventRecommendations()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        var service = Service(database);
        Assert.Empty(await service.GetSection(UnknownSection, Guid.NewGuid()));
        Assert.Empty(await service.GetSection(RecommendationConstants.Friends, Guid.NewGuid()));
        Assert.Empty(await service.GetRecentQuick(Guid.NewGuid(), default));
        Assert.Empty(await service.GetRecentQuick(Guid.NewGuid(), NegativeQuickCount));
    }

    [PostgresFact]
    public async Task EmptyCatalogAndNewAccountProduceNoEmptySections()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var service = Service(database);
        var accountIdentifier = Guid.NewGuid();
        Assert.Empty(await service.GetSections(accountIdentifier));
        Assert.Empty(await service.GetSection(RecommendationConstants.Today, accountIdentifier));
        Assert.Empty(await service.GetSection(RecommendationConstants.Artists, accountIdentifier));
        Assert.Empty(await service.GetSection(RecommendationConstants.Saved, accountIdentifier));
    }

    [PostgresFact]
    public async Task UnknownContextDoesNotMarkRecordingAlbumRecentlyPlayed()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var album = AddAlbum(database);
        var recording = AddRecording(database, album);
        AddListen(database, owner, recording);
        await database.SaveChangesAsync();
        var service = Service(database);
        Assert.Empty(await service.GetRecentQuick(owner.Id, RecommendationConstants.MaximumQuickItemLimit));
        Assert.Empty(await service.GetSection(RecommendationConstants.Recent, owner.Id));
        Assert.DoesNotContain(await service.GetSections(owner.Id), section => section.Id == RecommendationConstants.Recent);
    }

    [PostgresFact]
    public async Task PlaylistPlaybackMarksPlaylistAndExplicitAlbumPlaybackMarksAlbum()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var album = AddAlbum(database);
        var recording = AddRecording(database, album);
        var playlist = AddPlaylist(database, owner);
        AddListen(database, owner, recording, playlist: playlist, playedAt: FirstPlayedAt.AddMinutes(RecentMinuteOffset));
        await database.SaveChangesAsync();
        var service = Service(database);
        var playlistResponse = Assert.Single(await service.GetRecentQuick(owner.Id, RecommendationConstants.MaximumQuickItemLimit));
        Assert.Equal(playlist.Id, playlistResponse.Id);
        Assert.Equal(RecommendationConstants.PlaylistType, playlistResponse.Type);
        Assert.True(playlistResponse.CanEdit);
        AddListen(database, owner, recording, album);
        await database.SaveChangesAsync();
        var recent = await service.GetSection(RecommendationConstants.Recent, owner.Id);
        Assert.Equal([playlist.Id, album.Id], recent.Select(collection => collection.Id));
        Assert.Equal(recent.Select(collection => collection.Id),
            (await service.GetRecentQuick(owner.Id, RecommendationConstants.SectionItemLimit)).Select(collection => collection.Id));
    }

    [PostgresFact]
    public async Task RepeatedPlaysCannotStarveOlderCollectionsAndTypeIsPartOfIdentity()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var album = AddAlbum(database);
        var recording = AddRecording(database, album);
        var sameIdentifierPlaylist = AddPlaylist(database, owner, album.Id);
        AddListen(database, owner, recording, playlist: sameIdentifierPlaylist);
        foreach (var offset in Enumerable.Range(default, RepeatedListenCount))
            AddListen(database, owner, recording, album, playedAt: FirstPlayedAt.AddMinutes(offset + RecentMinuteOffset));
        await database.SaveChangesAsync();
        var recent = await Service(database).GetRecentQuick(owner.Id, RecommendationConstants.MaximumQuickItemLimit);
        Assert.Equal([RecommendationConstants.AlbumType, RecommendationConstants.PlaylistType], recent.Select(collection => collection.Type));
        Assert.All(recent, collection => Assert.Equal(album.Id, collection.Id));
    }

    [PostgresFact]
    public async Task ForeignAndDeletedCollectionsAndOtherAccountsAreExcludedBeforeLimiting()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var foreignOwner = AddOwner(database);
        var album = AddAlbum(database);
        var recording = AddRecording(database, album);
        var foreignPlaylist = AddPlaylist(database, foreignOwner);
        var deletedAlbumIdentifier = Guid.NewGuid();
        var deletedPlaylistIdentifier = Guid.NewGuid();
        AddListen(database, owner, recording, playlist: foreignPlaylist);
        var deletedAlbumListen = AddListen(database, owner, recording);
        deletedAlbumListen.SourceAlbumId = deletedAlbumIdentifier;
        var deletedPlaylistListen = AddListen(database, owner, recording);
        deletedPlaylistListen.PlaylistId = deletedPlaylistIdentifier;
        AddListen(database, foreignOwner, recording, album);
        await database.SaveChangesAsync();
        Assert.Empty(await Service(database).GetRecentQuick(owner.Id, RecommendationConstants.MaximumQuickItemLimit));
    }

    [PostgresFact]
    public async Task RecentCollectionsUseNewestPlayAndStableTypeAndIdentifierTies()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var olderAlbum = AddAlbum(database);
        var latestAlbum = AddAlbum(database);
        var olderRecording = AddRecording(database, olderAlbum);
        var latestRecording = AddRecording(database, latestAlbum);
        AddListen(database, owner, olderRecording, olderAlbum);
        AddListen(database, owner, latestRecording, latestAlbum, playedAt: FirstPlayedAt.AddMinutes(RecentMinuteOffset));
        AddListen(database, owner, olderRecording, olderAlbum, playedAt: FirstPlayedAt.AddMinutes(RecentMinuteOffset));
        await database.SaveChangesAsync();
        var service = Service(database);
        var expectedIdentifiers = new[] { olderAlbum.Id, latestAlbum.Id }.Order().ToArray();
        var first = await service.GetRecentQuick(owner.Id, RecommendationConstants.SectionItemLimit);
        var second = await service.GetRecentQuick(owner.Id, RecommendationConstants.SectionItemLimit);
        Assert.Equal(expectedIdentifiers, first.Select(collection => collection.Id));
        Assert.Equal(first.Select(collection => collection.Id), second.Select(collection => collection.Id));
    }

    [PostgresFact]
    public async Task NewListenerGetsStableLatestAlbumsWithoutFakePersonalOrFriendsSections()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var albums = Enumerable.Range(default, FixtureAlbumCount).Select(albumOrdinal => AddAlbum(database)).ToArray();
        await database.SaveChangesAsync();
        var service = Service(database);
        var section = Assert.Single(await service.GetSections(owner.Id));
        Assert.Equal(RecommendationConstants.Latest, section.Id);
        Assert.Equal(RecommendationConstants.LatestTitle, section.Title);
        var expected = albums.OrderBy(album => album.Id).Take(RecommendationConstants.SectionItemLimit).Select(album => album.Id);
        var latest = await service.GetSection(RecommendationConstants.Latest, owner.Id);
        Assert.Equal(expected, latest.Select(album => album.Id));
        Assert.Equal(latest.Select(album => album.Id), (await service.GetSection(RecommendationConstants.Today, owner.Id)).Select(album => album.Id));
        Assert.Equal(latest.Select(album => album.Id), (await service.GetSection(RecommendationConstants.Picks, owner.Id)).Select(album => album.Id));
        Assert.Empty(await service.GetSection(RecommendationConstants.Friends, owner.Id));
    }

    [PostgresFact]
    public async Task SavedAlbumsUseSavedTimeAndAccountScope()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var foreignOwner = AddOwner(database);
        var older = AddAlbum(database);
        var newer = AddAlbum(database);
        var foreign = AddAlbum(database);
        database.UserSavedAlbums.AddRange(new UserSavedAlbum { User = owner, Release = older, SavedAt = FirstPlayedAt },
            new UserSavedAlbum { User = owner, Release = newer, SavedAt = FirstPlayedAt.AddMinutes(RecentMinuteOffset) },
            new UserSavedAlbum { User = foreignOwner, Release = foreign, SavedAt = FirstPlayedAt });
        await database.SaveChangesAsync();
        var service = Service(database);
        Assert.Equal([newer.Id, older.Id], (await service.GetSection(RecommendationConstants.Saved, owner.Id)).Select(album => album.Id));
        Assert.Contains(await service.GetSections(owner.Id), section => section.Id == RecommendationConstants.Saved);
    }

    [PostgresFact]
    public async Task ArtistRecommendationsUseListensLikesAndSavedEvidenceWithoutForeignOrUnrelatedAlbums()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var foreignOwner = AddOwner(database);
        var listenedAlbum = AddAlbum(database);
        var likedAlbum = AddAlbum(database);
        var savedAlbum = AddAlbum(database);
        var savedArtistOtherAlbum = AddAlbum(database, savedAlbum.Artist);
        var foreignAlbum = AddAlbum(database);
        var unrelatedAlbum = AddAlbum(database);
        var listenedRecording = AddRecording(database, listenedAlbum);
        var likedRecording = AddRecording(database, likedAlbum);
        var foreignRecording = AddRecording(database, foreignAlbum);
        AddListen(database, owner, listenedRecording);
        AddListen(database, foreignOwner, foreignRecording);
        var likes = AddPlaylist(database, owner);
        likes.IsSystem = true;
        likes.PlaylistRecordings.Add(new PlaylistRecording { RecordingId = likedRecording.Id, AddedById = owner.Id });
        database.UserSavedAlbums.Add(new UserSavedAlbum { User = owner, Release = savedAlbum, SavedAt = FirstPlayedAt });
        await database.SaveChangesAsync();
        var service = Service(database);
        var expected = new[] { listenedAlbum.Id, likedAlbum.Id, savedArtistOtherAlbum.Id }.Order();
        var recommendations = await service.GetSection(RecommendationConstants.Artists, owner.Id);
        Assert.Equal(expected, recommendations.Select(album => album.Id));
        Assert.DoesNotContain(recommendations, album => album.Id == savedAlbum.Id || album.Id == unrelatedAlbum.Id || album.Id == foreignAlbum.Id);
        Assert.Contains(await service.GetSections(owner.Id), section => section.Id == RecommendationConstants.Artists);
        Assert.Equal(recommendations.Select(album => album.Id), (await service.GetSection(RecommendationConstants.Today, owner.Id)).Select(album => album.Id));
    }

    [PostgresFact]
    public async Task OrdinaryAndLaterDuplicateLikedPlaylistsDoNotSupplyArtistEvidence()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var selectedAlbum = AddAlbum(database);
        var ordinaryAlbum = AddAlbum(database);
        var duplicateAlbum = AddAlbum(database);
        var selectedRecording = AddRecording(database, selectedAlbum);
        var ordinaryRecording = AddRecording(database, ordinaryAlbum);
        var duplicateRecording = AddRecording(database, duplicateAlbum);
        var selected = AddPlaylist(database, owner);
        selected.IsSystem = true;
        selected.CreatedAt = FirstPlayedAt;
        selected.PlaylistRecordings.Add(new PlaylistRecording { RecordingId = selectedRecording.Id, AddedById = owner.Id });
        var ordinary = AddPlaylist(database, owner);
        ordinary.PlaylistRecordings.Add(new PlaylistRecording { RecordingId = ordinaryRecording.Id, AddedById = owner.Id });
        var duplicate = AddPlaylist(database, owner);
        duplicate.IsSystem = true;
        duplicate.CreatedAt = FirstPlayedAt.AddMinutes(RecentMinuteOffset);
        duplicate.PlaylistRecordings.Add(new PlaylistRecording { RecordingId = duplicateRecording.Id, AddedById = owner.Id });
        await database.SaveChangesAsync();
        var response = Assert.Single(await Service(database).GetSection(RecommendationConstants.Artists, owner.Id));
        Assert.Equal(selectedAlbum.Id, response.Id);
    }

    [PostgresFact]
    public async Task RecentQuickClampsResultsAndLatestOrdersByAddedTime()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var albums = Enumerable.Range(default, FixtureAlbumCount).Select(offset =>
        {
            var album = AddAlbum(database);
            album.DateAdded = FirstPlayedAt.AddMinutes(offset);
            AddListen(database, owner, AddRecording(database, album), album, playedAt: album.DateAdded);
            return album;
        }).ToArray();
        await database.SaveChangesAsync();
        var service = Service(database);
        var recent = await service.GetRecentQuick(owner.Id, LargeQuickCount);
        Assert.Equal(RecommendationConstants.MaximumQuickItemLimit, recent.Count);
        Assert.Equal(albums.Reverse().Take(RecommendationConstants.MaximumQuickItemLimit).Select(album => album.Id), recent.Select(album => album.Id));
        Assert.Equal(albums.Reverse().Take(RecommendationConstants.SectionItemLimit).Select(album => album.Id),
            (await service.GetSection(RecommendationConstants.Latest, owner.Id)).Select(album => album.Id));
    }

    private static User AddOwner(StreamingDbContext database)
    {
        var identifier = Guid.NewGuid();
        var owner = new User { Id = identifier, Username = identifier.ToString(), Email = identifier + FixtureEmailSuffix,
            PasswordHash = UnusedCredential, ProviderId = UnusedCredential };
        database.Users.Add(owner);
        return owner;
    }

    private static Release AddAlbum(StreamingDbContext database, Artist? artist = null)
    {
        var album = new Release { Id = Guid.NewGuid(), Title = FixtureName, Cover = FixtureCover, DateAdded = FirstPlayedAt,
            Artist = artist ?? new Artist { Id = Guid.NewGuid(), Name = FixtureName } };
        database.Releases.Add(album);
        return album;
    }

    private static Recording AddRecording(StreamingDbContext database, Release album)
    {
        var recording = new Recording { Id = Guid.NewGuid(), Release = album, Title = FixtureName, Cover = FixtureCover };
        database.Recordings.Add(recording);
        return recording;
    }

    private static Playlist AddPlaylist(StreamingDbContext database, User owner, Guid? identifier = null)
    {
        var playlist = new Playlist { Id = identifier ?? Guid.NewGuid(), Owner = owner, Name = FixtureName };
        database.Playlists.Add(playlist);
        return playlist;
    }

    private static Listen AddListen(StreamingDbContext database, User owner, Recording recording,
        Release? album = null, Playlist? playlist = null, DateTime? playedAt = null)
    {
        var listen = new Listen { ListenId = Guid.NewGuid(), User = owner, RecordingId = recording.Id,
            SourceAlbumId = album?.Id, PlaylistId = playlist?.Id, StartTime = playedAt ?? FirstPlayedAt,
            LastProgressUpdate = playedAt ?? FirstPlayedAt, Artist = FixtureName, Album = FixtureName, TrackName = FixtureName };
        database.Listens.Add(listen);
        return listen;
    }

    private static RecommendationService Service(StreamingDbContext database) => new(database, new FixtureImages());

    private sealed class FixtureImages : IImageService
    {
        public string GetPreferredCover(string? local, string? optimized, string? original) => original ?? FixtureCover;
        public AlbumCoversDto GetAlbumCover(Guid releaseId, Guid? releaseGroupId = null) => new() { Cover = FixtureCover };
        public AlbumCoversDto GetAllAlbumCovers(Guid releaseGroupId) => new() { Cover = FixtureCover };
    }
}
