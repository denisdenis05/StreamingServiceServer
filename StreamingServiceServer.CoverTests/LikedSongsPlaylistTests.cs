using System.Reflection;
using Npgsql;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocialMedia.Data.Models;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models.Library;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class LikedSongsPlaylistTests
{
    private const string PlaylistName = "Selected collection";
    private const string AccountEmail = "liked-playlist@test.invalid";
    private const string UnusedCredential = "unused";
    private const int FirstPage = 1;
    private const int PageSize = 10;

    [Fact]
    public void SelectionChangesDesignationAndPreservesRecordingsAndNames()
    {
        var previous = Playlist(Guid.NewGuid(), true);
        var selected = Playlist(previous.OwnerId);
        var previousRecording = new PlaylistRecording { RecordingId = Guid.NewGuid() };
        var selectedRecording = new PlaylistRecording { RecordingId = Guid.NewGuid() };
        previous.PlaylistRecordings.Add(previousRecording);
        selected.PlaylistRecordings.Add(selectedRecording);
        LikedSongsPlaylistSelection.Apply([previous, selected], selected.Id);
        Assert.False(previous.IsSystem);
        Assert.True(selected.IsSystem);
        Assert.Same(previousRecording, Assert.Single(previous.PlaylistRecordings));
        Assert.Same(selectedRecording, Assert.Single(selected.PlaylistRecordings));
        Assert.Equal(PlaylistName, selected.Name);
    }

    [Fact]
    public void ClearingSelectionPreservesEveryPlaylist()
    {
        var selected = Playlist(Guid.NewGuid(), true);
        LikedSongsPlaylistSelection.Apply([selected], null);
        Assert.False(selected.IsSystem);
        Assert.Equal(PlaylistName, selected.Name);
    }

    [Fact]
    public void MissingSelectionFailsBeforeChangingExistingDesignation()
    {
        var selected = Playlist(Guid.NewGuid(), true);
        Assert.Throws<KeyNotFoundException>(() => LikedSongsPlaylistSelection.Apply([selected], Guid.NewGuid()));
        Assert.True(selected.IsSystem);
    }

    [Fact]
    public void RepeatedSelectionNormalizesLegacyDuplicateDesignations()
    {
        var selected = Playlist(Guid.NewGuid(), true);
        var duplicate = Playlist(selected.OwnerId, true);
        LikedSongsPlaylistSelection.Apply([selected, duplicate], selected.Id);
        LikedSongsPlaylistSelection.Apply([selected, duplicate], selected.Id);
        Assert.True(selected.IsSystem);
        Assert.False(duplicate.IsSystem);
    }

    [Fact]
    public async Task EmptyIdentifierFailsWithoutDatabaseAccess()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(database).SetLikedSongsPlaylistAsync(Guid.NewGuid(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ControllerRejectsMissingAccountAndEmptyPlaylistWithoutCallingService()
    {
        var controller = new LibraryController(null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        Assert.IsType<UnauthorizedObjectResult>(await controller.GetLikedSongsPlaylist(CancellationToken.None));
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())]));
        Assert.IsType<BadRequestObjectResult>(await controller.SetLikedSongsPlaylist(new LikedSongsPlaylistSetting(Guid.Empty), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.GetRecordingLike(Guid.Empty, CancellationToken.None));
    }

    [PostgresFact]
    public async Task SelectionPersistsAndAllHeartQueriesAndTogglesUseItsContents()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var previous = Playlist(owner.Id, true);
        var selected = Playlist(owner.Id);
        var previousRecording = Guid.NewGuid();
        var selectedRecording = Guid.NewGuid();
        previous.PlaylistRecordings.Add(new PlaylistRecording { RecordingId = previousRecording, AddedById = owner.Id });
        selected.PlaylistRecordings.Add(new PlaylistRecording { RecordingId = selectedRecording, AddedById = owner.Id });
        database.Playlists.AddRange(previous, selected);
        await database.SaveChangesAsync();
        var service = Service(database);
        Assert.Equal(previous.Id, (await service.GetLikedSongsPlaylistAsync(owner.Id, CancellationToken.None)).PlaylistId);
        await service.SetLikedSongsPlaylistAsync(owner.Id, selected.Id, CancellationToken.None);
        database.ChangeTracker.Clear();
        Assert.Equal(selected.Id, (await service.GetLikedSongsPlaylistAsync(owner.Id, CancellationToken.None)).PlaylistId);
        Assert.Equal([selectedRecording], await service.GetLikedRecordingIdsAsync(owner.Id, CancellationToken.None));
        Assert.Equal([selectedRecording], await service.GetLikedRecordingIdsAsync(owner.Id, [selectedRecording, previousRecording], CancellationToken.None));
        var controller = new LibraryController(service) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner.Id.ToString())]));
        var likeResponse = Assert.IsType<OkObjectResult>(await controller.GetRecordingLike(selectedRecording, CancellationToken.None));
        Assert.True(Assert.IsType<RecordingLikeStatus>(likeResponse.Value).IsLiked);
        Assert.True(Assert.Single(await service.GetPlaylistRecordingsAsync(selected.Id, owner.Id, FirstPage, PageSize, CancellationToken.None)).IsLiked);
        await service.ToggleLikeAsync(owner.Id, selectedRecording, new AddSongToPlaylistRequest(), CancellationToken.None);
        Assert.Empty(await service.GetLikedRecordingIdsAsync(owner.Id, CancellationToken.None));
        await service.AddRecordingsToPlaylistAsync(owner.Id, selected.Id, [new AddSongToPlaylistRequest { Id = selectedRecording }], CancellationToken.None);
        Assert.Contains(selectedRecording, await service.GetLikedRecordingIdsAsync(owner.Id, CancellationToken.None));
        await service.RemoveRecordingFromPlaylistAsync(owner.Id, selected.Id, selectedRecording, CancellationToken.None);
        Assert.Empty(await service.GetLikedRecordingIdsAsync(owner.Id, CancellationToken.None));
        Assert.True(await database.PlaylistRecordings.AnyAsync(recording => recording.PlaylistId == previous.Id && recording.RecordingId == previousRecording));
    }

    [PostgresFact]
    public async Task ForeignAndDeletedPlaylistsCannotReplaceSelectionAndClearingCreatesDefaultOnNextLike()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var otherOwner = AddOwner(database);
        var selected = Playlist(owner.Id, true);
        var foreign = Playlist(otherOwner.Id);
        database.Playlists.AddRange(selected, foreign);
        await database.SaveChangesAsync();
        var service = Service(database);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetLikedSongsPlaylistAsync(owner.Id, foreign.Id, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetLikedSongsPlaylistAsync(owner.Id, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(selected.Id, (await service.GetLikedSongsPlaylistAsync(owner.Id, CancellationToken.None)).PlaylistId);
        await service.UpdatePlaylistAsync(owner.Id, selected.Id, PlaylistName, CancellationToken.None);
        await service.SetLikedSongsPlaylistAsync(owner.Id, null, CancellationToken.None);
        Assert.Null((await service.GetLikedSongsPlaylistAsync(owner.Id, CancellationToken.None)).PlaylistId);
        await service.ToggleLikeAsync(owner.Id, Guid.NewGuid(), new AddSongToPlaylistRequest(), CancellationToken.None);
        var defaultSelection = await service.GetLikedSongsPlaylistAsync(owner.Id, CancellationToken.None);
        Assert.NotNull(defaultSelection.PlaylistId);
        Assert.NotEqual(selected.Id, defaultSelection.PlaylistId);
        Assert.True(await database.Playlists.AnyAsync(playlist => playlist.Id == selected.Id));
        await service.DeletePlaylistAsync(owner.Id, defaultSelection.PlaylistId!.Value, CancellationToken.None);
        Assert.Null((await service.GetLikedSongsPlaylistAsync(owner.Id, CancellationToken.None)).PlaylistId);
    }

    [PostgresFact]
    public async Task ConcurrentSelectionsCannotLeaveTwoDesignatedPlaylists()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = AddOwner(database);
        var first = Playlist(owner.Id);
        var second = Playlist(owner.Id);
        database.Playlists.AddRange(first, second);
        await database.SaveChangesAsync();
        var connectionString = database.Database.GetConnectionString();
        var options = new DbContextOptionsBuilder<StreamingDbContext>().UseNpgsql(connectionString).Options;
        await using var firstDatabase = new StreamingDbContext(options);
        await using var secondDatabase = new StreamingDbContext(options);
        await Task.WhenAll(SelectAllowingSerializationFailure(firstDatabase, owner.Id, first.Id),
            SelectAllowingSerializationFailure(secondDatabase, owner.Id, second.Id));
        database.ChangeTracker.Clear();
        Assert.Single(await database.Playlists.Where(playlist => playlist.OwnerId == owner.Id && playlist.IsSystem).ToListAsync());
    }

    private static async Task SelectAllowingSerializationFailure(StreamingDbContext database, Guid ownerId, Guid playlistId)
    {
        try
        {
            await Service(database).SetLikedSongsPlaylistAsync(ownerId, playlistId, CancellationToken.None);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure })
        {
        }
    }

    private static User AddOwner(StreamingDbContext database)
    {
        var owner = new User { Id = Guid.NewGuid(), Username = Guid.NewGuid().ToString(), Email = Guid.NewGuid() + AccountEmail,
            PasswordHash = UnusedCredential, ProviderId = UnusedCredential };
        database.Users.Add(owner);
        return owner;
    }

    private static Playlist Playlist(Guid ownerId, bool selected = false) => new()
    {
        Id = Guid.NewGuid(), OwnerId = ownerId, Name = PlaylistName, IsSystem = selected
    };

    private static LibraryService Service(StreamingDbContext database) => new(database,
        DispatchProxy.Create<IExternalMusicSearchService, UnusedSearchProvider>());

    public class UnusedSearchProvider : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => throw new NotSupportedException();
    }
}
