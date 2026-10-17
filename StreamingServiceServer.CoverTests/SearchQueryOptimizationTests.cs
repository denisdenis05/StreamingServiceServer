using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SocialMedia.Data.Models;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models.Library;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class SearchQueryOptimizationTests
{
    [Fact]
    public async Task EmptyCandidateLikesDoNotNeedADatabaseProvider()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        var library = new LibraryService(database, Provider());
        Assert.Empty(await library.GetLikedRecordingIdsAsync(Guid.NewGuid(), Array.Empty<Guid>(), CancellationToken.None));
    }

    [Fact]
    public async Task CanceledSearchesDoNotStartProviderOrDatabaseWork()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        var provider = Provider();
        var service = new MetadataService(database, provider, new Images(), new LibraryService(database, provider));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GeneralSearch("query", 10, Guid.NewGuid(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DbSearch("query", 1, 10, Guid.NewGuid(), cancellation.Token));
        Assert.Equal(0, ((SearchProvider)provider).Calls);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task CandidateLikesReadOnlyMatchingIdentifiersAndKeepAccountIsolationWithoutTrackingEntities()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = Guid.NewGuid(); var stranger = Guid.NewGuid(); var identifiers = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
        AddPlaylist(database, owner, true, identifiers); AddPlaylist(database, stranger, true, new[] { identifiers[99], Guid.NewGuid() });
        AddPlaylist(database, owner, false, new[] { Guid.NewGuid() });
        await database.SaveChangesAsync(); database.ChangeTracker.Clear();
        var library = new LibraryService(database, Provider());
        Assert.Equal(new[] { identifiers[50] }, await library.GetLikedRecordingIdsAsync(owner, new[] { identifiers[50], Guid.NewGuid() }, CancellationToken.None));
        Assert.Empty(await library.GetLikedRecordingIdsAsync(Guid.NewGuid(), identifiers, CancellationToken.None));
        Assert.Equal(100, (await library.GetLikedRecordingIdsAsync(owner, CancellationToken.None)).Count);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task PlaylistPagesOnlyQueryLikesForTheirReturnedRecordings()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope(); var seeded = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = Guid.NewGuid(); var identifiers = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
        AddPlaylist(seeded, owner, true, identifiers);
        var ordinary = AddPlaylist(seeded, owner, false, new[] { identifiers[8], Guid.NewGuid(), identifiers[70] }, addOwner: false);
        await seeded.SaveChangesAsync();
        var capture = new QueryCapture();
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().UseNpgsql(seeded.Database.GetConnectionString()).AddInterceptors(capture).Options);
        var page = await new LibraryService(database, Provider()).GetPlaylistRecordingsAsync(ordinary.Id, owner, 1, 2, CancellationToken.None);
        Assert.Equal(2, page.Count); Assert.True(page[0].IsLiked); Assert.False(page[1].IsLiked);
        var candidateSet = Assert.Single(capture.IdentifierSets); Assert.Equal(2, candidateSet.Length); Assert.DoesNotContain(identifiers[70], candidateSet);
        Assert.DoesNotContain(database.ChangeTracker.Entries(), entry => entry.Entity is Playlist or PlaylistRecording);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task GeneralSearchStartsProvidersTogetherAndSerializesSharedContextReads()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope(); var seeded = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = Guid.NewGuid(); var liked = Guid.NewGuid(); var omitted = Guid.NewGuid();
        AddPlaylist(seeded, owner, true, new[] { liked, omitted }); await seeded.SaveChangesAsync();
        var capture = new QueryCapture { DelayFirstQuery = true };
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().UseNpgsql(seeded.Database.GetConnectionString()).AddInterceptors(capture).Options);
        var provider = Provider(); ((SearchProvider)provider).Recordings = new() { new() { Id = liked, Title = "first" }, new() { Id = omitted, Title = "second" } };
        var service = new MetadataService(database, provider, new Images(), new LibraryService(database, provider));
        var request = service.GeneralSearch("Liked", 1, owner);
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, ((SearchProvider)provider).Calls); Assert.Equal(1, capture.Commands); Assert.False(request.IsCompleted);
        capture.Release.TrySetResult();
        var response = await request;
        Assert.Single(response.Playlists); Assert.True(Assert.Single(response.Tracks).IsLiked);
        Assert.Equal(new[] { liked }, Assert.Single(capture.IdentifierSets));
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task CancellationInterruptsAnActivePlaylistReadBeforeLikesStart()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); using var scope = fixture.Services.CreateScope();
        var seeded = scope.ServiceProvider.GetRequiredService<StreamingDbContext>(); var capture = new QueryCapture { DelayFirstQuery = true };
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().UseNpgsql(seeded.Database.GetConnectionString()).AddInterceptors(capture).Options);
        var provider = Provider(); var service = new MetadataService(database, provider, new Images(), new LibraryService(database, provider));
        using var cancellation = new CancellationTokenSource(); var request = service.GeneralSearch("query", 1, Guid.NewGuid(), cancellation.Token);
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); Assert.Equal(1, capture.Commands);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task DatabaseSearchBoundsLikeCandidatesToReturnedExactAndFuzzyTracks()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); using var scope = fixture.Services.CreateScope();
        var seeded = scope.ServiceProvider.GetRequiredService<StreamingDbContext>(); var owner = Guid.NewGuid();
        var artist = new Artist { Id = Guid.NewGuid(), Name = "performer", SortName = "performer" };
        var release = new Release { Id = Guid.NewGuid(), Title = "album", Artist = artist, Cover = string.Empty };
        var exact = new Recording { Id = Guid.NewGuid(), Title = "needle", Release = release, Cover = string.Empty };
        var fuzzy = new Recording { Id = Guid.NewGuid(), Title = "needles", Release = release, Cover = string.Empty };
        seeded.Recordings.AddRange(exact, fuzzy);
        AddPlaylist(seeded, owner, true, new[] { exact.Id, fuzzy.Id }.Concat(Enumerable.Range(0, 100).Select(_ => Guid.NewGuid())).ToArray());
        await seeded.SaveChangesAsync();
        var capture = new QueryCapture();
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().UseNpgsql(seeded.Database.GetConnectionString()).AddInterceptors(capture).Options);
        var provider = Provider(); var service = new MetadataService(database, provider, new Images(), new LibraryService(database, provider));
        var response = await service.DbSearch("needle", 1, 1, owner);
        Assert.Single(response.Tracks); Assert.NotEmpty(response.FuzzyTracks);
        var returned = response.Tracks.Concat(response.FuzzyTracks).ToList(); Assert.All(returned, recording => Assert.True(recording.IsLiked));
        var candidates = capture.IdentifierSets.Last();
        Assert.Equal(returned.Select(recording => recording.Id).Order(), candidates.Order());
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task CancellationStopsWaitingForUncancellableProviderResultsBeforeLikesStart()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(); using var scope = fixture.Services.CreateScope();
        var seeded = scope.ServiceProvider.GetRequiredService<StreamingDbContext>(); var capture = new QueryCapture();
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().UseNpgsql(seeded.Database.GetConnectionString()).AddInterceptors(capture).Options);
        var provider = Provider(); var completion = new TaskCompletionSource<List<RecordingDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((SearchProvider)provider).RecordingResults = completion.Task;
        var service = new MetadataService(database, provider, new Images(), new LibraryService(database, provider));
        using var cancellation = new CancellationTokenSource(); var request = service.GeneralSearch("query", 1, Guid.NewGuid(), cancellation.Token);
        await capture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); Assert.Equal(1, capture.Commands);
        Assert.False(completion.Task.IsCompleted); completion.TrySetResult(new());
    }

    private static Playlist AddPlaylist(StreamingDbContext database, Guid owner, bool system, Guid[] recordings, bool addOwner = true)
    {
        if (addOwner && !database.Users.Local.Any(user => user.Id == owner)) database.Users.Add(new User { Id = owner, Username = owner.ToString(), Email = owner + "@test.invalid", PasswordHash = "unused", ProviderId = "unused" });
        var playlist = new Playlist { Id = Guid.NewGuid(), OwnerId = owner, IsSystem = system, Name = system ? "Liked songs" : "ordinary" };
        foreach (var recording in recordings.Select((identifier, order) => new PlaylistRecording { RecordingId = identifier, Order = order, AddedById = owner })) playlist.PlaylistRecordings.Add(recording);
        database.Playlists.Add(playlist); return playlist;
    }
    private static IExternalMusicSearchService Provider() => DispatchProxy.Create<IExternalMusicSearchService, SearchProvider>();
    public class SearchProvider : DispatchProxy
    {
        public int Calls { get; private set; }
        public List<RecordingDto> Recordings { get; set; } = new();
        public Task<List<RecordingDto>>? RecordingResults { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            Calls++;
            return method?.Name switch {
                nameof(IExternalMusicSearchService.SearchArtistsAsync) => Task.FromResult(new List<ArtistDto>()),
                nameof(IExternalMusicSearchService.SearchAlbumsAsync) => Task.FromResult(new List<ReleaseDto>()),
                nameof(IExternalMusicSearchService.SearchRecordingsAsync) => RecordingResults ?? Task.FromResult(Recordings),
                _ => throw new InvalidOperationException("Unexpected provider method") };
        }
    }
    private sealed class Images : IImageService
    {
        public string GetPreferredCover(string? local, string? optimized, string? original) => original ?? string.Empty;
        public AlbumCoversDto GetAlbumCover(Guid releaseId, Guid? releaseGroupId = null) => new();
        public AlbumCoversDto GetAllAlbumCovers(Guid releaseGroupId) => new();
    }
    private sealed class QueryCapture : DbCommandInterceptor
    {
        public bool DelayFirstQuery { get; init; }
        public int Commands { get; private set; }
        public List<Guid[]> IdentifierSets { get; } = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Completed.TrySetResult(); return ValueTask.FromResult(result);
        }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            foreach (DbParameter parameter in command.Parameters) if (parameter.Value is Guid[] identifiers) IdentifierSets.Add(identifiers);
            if (DelayFirstQuery && Commands == 1) { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
}
