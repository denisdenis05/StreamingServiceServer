using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using SocialMedia.Data.Models;
using StreamingServiceServer.API.Recommendations;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Business.Services.Recommendations;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class RecommendationPreviewCacheTests
{
    private static readonly RecommendationRequest Calm = new(RecommendationPolicy.Calm);
    private const string FixtureName = "Preview cache fixture";

    [PostgresFact]
    public async Task RepeatedFetchesAndNewServiceScopesReusePersistedTracksAndArtwork()
    {
        await using var fixture = await Fixture.CreateAsync();
        var generated = Preview(Calm);
        var calls = 0;
        Task<RecommendationPlaylistPreview> Generate(CancellationToken token) { calls++; return Task.FromResult(generated); }
        using (var scope = fixture.Scope())
        {
            var first = await fixture.Cache(scope).GetAsync(fixture.Owner, Calm, Generate, false, default);
            Assert.Equal(generated.Cover, first.Cover);
        }
        using (var restarted = fixture.Scope())
        {
            var cached = await fixture.Cache(restarted).GetAsync(fixture.Owner, Calm, Generate, false, default);
            Assert.Equal(generated.Page.Tracks.Select(track => track.Id), cached.Page.Tracks.Select(track => track.Id));
            Assert.Equal(generated.Cover, cached.Cover);
        }
        Assert.Equal(1, calls);
    }

    [PostgresFact]
    public async Task HitsDoNotSlideExpiryAndMidnightRegeneratesTheSelection()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Scope();
        var cache = fixture.Cache(scope);
        var calls = 0;
        Task<RecommendationPlaylistPreview> Generate(CancellationToken token) { calls++; return Task.FromResult(Preview(Calm)); }
        var midnight = fixture.Clock.GetUtcNow().AddDays(1);
        var first = await cache.GetAsync(fixture.Owner, Calm, Generate, false, default);
        fixture.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        Assert.Equal(first.Cover, (await cache.GetAsync(fixture.Owner, Calm, Generate, false, default)).Cover);
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        Assert.Equal(midnight, await database.RecommendationPlaylistCaches.Select(row => row.ExpiresAt).SingleAsync());
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var renewed = await cache.GetAsync(fixture.Owner, Calm, Generate, false, default);
        Assert.NotEqual(first.Cover, renewed.Cover);
        Assert.Equal(2, calls);
        Assert.Equal(midnight.AddDays(1), await database.RecommendationPlaylistCaches.Select(row => row.ExpiresAt).SingleAsync());
    }

    [PostgresFact]
    public async Task AccountsAndDistinctRadioSeedsCannotShareSelections()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Scope();
        var cache = fixture.Cache(scope);
        var other = await fixture.AddUserAsync();
        var firstRequest = new RecommendationRequest(RecommendationPolicy.SongRadio, Guid.NewGuid());
        var secondRequest = firstRequest with { RecordingId = Guid.NewGuid() };
        var first = await cache.GetAsync(fixture.Owner, firstRequest, token => Task.FromResult(Preview(firstRequest)), false, default);
        var second = await cache.GetAsync(fixture.Owner, secondRequest, token => Task.FromResult(Preview(secondRequest)), false, default);
        var anotherUser = await cache.GetAsync(other, firstRequest, token => Task.FromResult(Preview(firstRequest)), false, default);
        Assert.NotEqual(first.Cover, second.Cover);
        Assert.NotEqual(first.Cover, anotherUser.Cover);
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().RecommendationPlaylistCaches.CountAsync());
    }

    [PostgresFact]
    public async Task ConcurrentColdFetchesAcrossConnectionsGenerateOnlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var firstScope = fixture.Scope();
        using var secondScope = fixture.Scope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<RecommendationPlaylistPreview> Generate(CancellationToken token)
        { Interlocked.Increment(ref calls); entered.TrySetResult(); await complete.Task.WaitAsync(token); return Preview(Calm); }
        var first = fixture.Cache(firstScope).GetAsync(fixture.Owner, Calm, Generate, false, default);
        await entered.Task;
        var second = fixture.Cache(secondScope).GetAsync(fixture.Owner, Calm, Generate, false, default);
        complete.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, calls);
        Assert.Equal(results[0].Cover, results[1].Cover);
    }

    [PostgresFact]
    public async Task ExplicitRefreshReplacesTheCachedSelectionAndDailyInvalidationPreservesOtherModes()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Scope();
        var cache = fixture.Cache(scope);
        var daily = new RecommendationRequest(RecommendationPolicy.Daily);
        var original = await cache.GetAsync(fixture.Owner, Calm, token => Task.FromResult(Preview(Calm)), false, default);
        var refreshed = await cache.GetAsync(fixture.Owner, Calm, token => Task.FromResult(Preview(Calm)), true, default);
        Assert.NotEqual(original.Cover, refreshed.Cover);
        var cached = await cache.GetAsync(fixture.Owner, Calm, token => throw new InvalidOperationException("Warm fetch must not generate."), false, default);
        Assert.Equal(refreshed.Cover, cached.Cover);
        await cache.GetAsync(fixture.Owner, daily, token => Task.FromResult(Preview(daily)), false, default);
        await cache.InvalidateDailyAsync(fixture.Owner, default);
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        Assert.Equal(RecommendationPolicy.Calm, await database.RecommendationPlaylistCaches.Select(row => row.Mode).SingleAsync());
    }

    [PostgresFact]
    public async Task FailedAndCanceledGenerationNeverCacheOrHoldTheAccountLock()
    {
        await using var fixture = await Fixture.CreateAsync();
        using (var scope = fixture.Scope())
        {
            var cache = fixture.Cache(scope);
            await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(fixture.Owner, Calm, token => throw new IOException("Fixture failure"), false, default));
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(fixture.Owner, Calm, token => {
                cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Preview(Calm));
            }, false, cancellation.Token));
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().RecommendationPlaylistCaches.ToArrayAsync());
        }
        using var nextScope = fixture.Scope();
        Assert.NotNull(await fixture.Cache(nextScope).GetAsync(fixture.Owner, Calm, token => Task.FromResult(Preview(Calm)), false, default));
    }

    [PostgresFact]
    public async Task GenerationCrossingMidnightCannotPublishYesterdayAsTodaysCache()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Scope();
        var cache = fixture.Cache(scope);
        await cache.GetAsync(fixture.Owner, Calm, token => {
            fixture.Clock.Advance(TimeSpan.FromDays(1)); return Task.FromResult(Preview(Calm));
        }, false, default);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().RecommendationPlaylistCaches.ToArrayAsync());
    }

    [PostgresFact]
    public async Task CachedPreviewsKeepCurrentHeartsSavedStatusAndUsableContinuationTokens()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Scope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var cache = fixture.Cache(scope);
        var daily = new RecommendationRequest(RecommendationPolicy.Daily);
        var version = Guid.NewGuid();
        var preview = Preview(daily) with { DailyVersion = version };
        var session = new RecommendationSession(fixture.Owner, Calm, Guid.NewGuid(), [], [], fixture.Clock.GetUtcNow() + RecommendationPolicy.SessionLifetime, RecommendationPolicy.PolicyVersion);
        preview = preview with { Page = preview.Page with { ContinuationToken = fixture.Cursors.Encode(session) } };
        await cache.GetAsync(fixture.Owner, daily, token => Task.FromResult(preview), false, default);
        var playlist = new Playlist { Id = Guid.NewGuid(), OwnerId = fixture.Owner, Name = FixtureName, IsSystem = true };
        database.Playlists.Add(playlist);
        database.PlaylistRecordings.Add(new PlaylistRecording { PlaylistId = playlist.Id, RecordingId = preview.Page.Tracks[0].Id, AddedById = fixture.Owner, Order = 0 });
        database.RecommendationDailyMixes.Add(new RecommendationDailyMix { UserId = fixture.Owner, Day = DateOnly.FromDateTime(fixture.Clock.GetUtcNow().UtcDateTime), Version = version, SavedPlaylistId = playlist.Id, RecordingIds = preview.Page.Tracks.Select(track => track.Id).ToArray(), CreatedAt = fixture.Clock.GetUtcNow() });
        await database.SaveChangesAsync();
        fixture.Clock.Advance(TimeSpan.FromHours(7));
        var cached = await cache.GetAsync(fixture.Owner, daily, token => throw new InvalidOperationException("Warm fetch must not generate."), false, default);
        Assert.True(cached.Page.Tracks[0].IsLiked);
        Assert.Equal(playlist.Id, cached.SavedPlaylistId);
        Assert.Equal(fixture.Clock.GetUtcNow() + RecommendationPolicy.SessionLifetime, fixture.Cursors.Decode(cached.Page.ContinuationToken!).ExpiresAt);
    }

    [PostgresFact]
    public async Task CleanupDeletesExpiredPayloadsButKeepsTodaysSelections()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Scope();
        var cache = fixture.Cache(scope);
        await cache.GetAsync(fixture.Owner, Calm, token => Task.FromResult(Preview(Calm)), false, default);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        var daily = new RecommendationRequest(RecommendationPolicy.Daily);
        await cache.GetAsync(fixture.Owner, daily, token => Task.FromResult(Preview(daily)), false, default);
        await cache.DeleteExpiredAsync(default);
        Assert.Equal(RecommendationPolicy.Daily, await scope.ServiceProvider.GetRequiredService<StreamingDbContext>().RecommendationPlaylistCaches.Select(row => row.Mode).SingleAsync());
    }

    [PostgresFact]
    public async Task MigrationCreatesTheDurableCacheAndItsExpiryIndex()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: true);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        Assert.Empty(await database.RecommendationPlaylistCaches.ToArrayAsync());
        Assert.Contains(await database.Database.GetAppliedMigrationsAsync(), migration => migration.EndsWith("AddRecommendationPlaylistCache", StringComparison.Ordinal));
    }

    private static RecommendationPlaylistPreview Preview(RecommendationRequest request) => new(request,
        new RecommendationPage([new RecordingResponse { Id = Guid.NewGuid(), Title = FixtureName }], null, FixtureName, "fixture", request.Mode),
        "https://fixture.invalid/playlist-" + Guid.NewGuid() + ".jpg", null, null);

    private sealed class Fixture(DatabaseFixture database) : IAsyncDisposable
    {
        public Guid Owner { get; private set; }
        public TestClock Clock => database.Clock;
        public IRecommendationCursorCodec Cursors { get; } = new RecommendationCursorCodec(new EphemeralDataProtectionProvider());
        public IServiceScope Scope() => database.Services.CreateScope();
        public RecommendationPreviewCache Cache(IServiceScope scope)
        {
            var context = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            return new RecommendationPreviewCache(context, Clock, new LibraryService(context, null!, null!), Cursors);
        }
        public async Task<Guid> AddUserAsync()
        {
            using var scope = Scope();
            var context = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var owner = new User { Id = Guid.NewGuid(), Username = FixtureName + Guid.NewGuid(), Email = Guid.NewGuid() + "@fixture.invalid", PasswordHash = FixtureName, ProviderId = FixtureName };
            context.Users.Add(owner);
            await context.SaveChangesAsync();
            return owner.Id;
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await DatabaseFixture.CreateAsync());
            fixture.Owner = await fixture.AddUserAsync();
            return fixture;
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
}
