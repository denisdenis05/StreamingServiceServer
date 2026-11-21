using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocialMedia.Data.Models;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.API.Recommendations;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Business.Services.Recommendations;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class AudioRecommendationLogicTests
{
    private const string Cohort = "test-cohort";
    private const string OtherCohort = "other-cohort";
    private const string Descriptor = "chill";
    private const string TestName = "Recommendation fixture";
    private const string FixtureCredential = "unused-fixture";
    private const string MusicPath = "Music:Path";
    private const string TemporaryFolder = "TEMP";
    private const string AudioExtension = ".flac";
    private const int CandidateCount = 24;
    private const int DesiredCount = 10;
    private static readonly Guid Seed = Guid.Parse("2da7ccf6-8c29-48f5-ac13-b1a37c1235e4");

    [Fact]
    public void NormalizationRejectsZeroAndNonfiniteVectorsAndPreservesDirection()
    {
        Assert.Null(RecommendationRanking.Normalize([0, 0]));
        Assert.Null(RecommendationRanking.Normalize([float.NaN, 1]));
        var normalized = RecommendationRanking.Normalize([3, 4]);
        Assert.NotNull(normalized);
        Assert.Equal(1, RecommendationRanking.Cosine(normalized, normalized), precision: 6);
    }

    [Fact]
    public void RankingIsDeterministicDeduplicatesContentAndIsolatesCohorts()
    {
        var candidates = Enumerable.Range(0, CandidateCount).Select(index => Feature()).ToList();
        candidates.Add(candidates[0] with { RecordingId = Guid.NewGuid() });
        candidates.Add(Feature() with { Cohort = OtherCohort });
        var ranking = new RecommendationRanking();
        var first = ranking.Select(candidates, [candidates[0]], RecommendationPolicy.SongRadio, Seed, DesiredCount);
        var repeated = ranking.Select(candidates, [candidates[0]], RecommendationPolicy.SongRadio, Seed, DesiredCount);
        Assert.Equal(first.Select(candidate => candidate.RecordingId), repeated.Select(candidate => candidate.RecordingId));
        Assert.Equal(first.Count, first.Select(candidate => candidate.ContentHash).Distinct().Count());
        Assert.All(first, candidate => Assert.Equal(Cohort, candidate.Cohort));
    }

    [Fact]
    public void RadioHonorsArtistAlbumLimitsAndSmartShuffleCanUseSingleAlbum()
    {
        var original = Feature();
        var candidates = Enumerable.Range(0, CandidateCount).Select(index => Feature() with { ArtistId = original.ArtistId, ReleaseId = original.ReleaseId }).ToArray();
        var ranking = new RecommendationRanking();
        Assert.Single(ranking.Select(candidates, [original], RecommendationPolicy.ArtistRadio, Seed, DesiredCount));
        Assert.Equal(DesiredCount, ranking.Select(candidates, [original], RecommendationPolicy.SmartShuffle, Seed, DesiredCount).Count);
    }

    [Fact]
    public void StoredDescriptorTransformRecoversAverageScoreForPinnedEngineOnly()
    {
        const double Average = 0.2;
        var stored = 1 / (1 + Math.Exp(-Average));
        Assert.Equal(Average, AudioRecommendationFeatures.RestoreDescriptor(stored, RecommendationPolicy.SupportedEngine), precision: 6);
        Assert.Equal(stored, AudioRecommendationFeatures.RestoreDescriptor(stored, OtherCohort));
        Assert.Equal(0, AudioRecommendationFeatures.RestoreDescriptor(double.NaN, RecommendationPolicy.SupportedEngine));
    }

    [Fact]
    public void FocusedModesUseEnergyAndDescriptorScoresWithoutChangingTheirStoredFeatures()
    {
        var calm = Feature() with { Energy = 0.2, Descriptors = new Dictionary<string, double> { [Descriptor] = 0.9 } };
        var energetic = Feature() with { Energy = 0.9, Descriptors = new Dictionary<string, double> { [Descriptor] = 0.1 } };
        var ranking = new RecommendationRanking();
        Assert.Equal(calm.RecordingId, ranking.Select([calm, energetic], [], RecommendationPolicy.Calm, Seed, 1).Single().RecordingId);
        Assert.Equal(energetic.RecordingId, ranking.Select([calm, energetic], [], RecommendationPolicy.Energy, Seed, 1).Single().RecordingId);
        Assert.Equal(0.9, calm.Descriptors[Descriptor]);
    }

    [Fact]
    public void CursorRoundTripIsOpaqueAndTamperingIsRejected()
    {
        var codec = new RecommendationCursorCodec(new EphemeralDataProtectionProvider());
        var session = new RecommendationSession(Seed, new(RecommendationPolicy.SongRadio, RecordingId: Seed), Seed, [Seed], [], DateTimeOffset.UnixEpoch, RecommendationPolicy.PolicyVersion);
        var token = codec.Encode(session);
        Assert.DoesNotContain(Seed.ToString(), token);
        Assert.Equal(session.UserId, codec.Decode(token).UserId);
        Assert.Throws<RecommendationInputException>(() => codec.Decode(token + "invalid"));
    }

    [Fact]
    public void ApiRequiresAuthenticatedUserOrAdministrator()
    {
        var authorization = typeof(AudioRecommendationsController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorization);
        Assert.Equal("Bearer", authorization.AuthenticationSchemes);
        Assert.Equal("User,Admin", authorization.Roles);
    }

    [PostgresFact]
    public async Task SongRadioPinsSeedExcludesRepeatedContentAndContinuationCannotCrossAccounts()
    {
        await using var fixture = await RecommendationFixture.CreateAsync();
        var seed = fixture.Features.Items[0];
        fixture.Features.Items[1] = fixture.Features.Items[1] with { ContentHash = seed.ContentHash, Vector = seed.Vector };
        var first = await fixture.Service.GenerateAsync(fixture.Owner.Id, new(RecommendationPolicy.SongRadio, RecordingId: seed.RecordingId, RandomSeed: Seed, Count: DesiredCount), CancellationToken.None);
        Assert.Equal(seed.RecordingId, first.Tracks[0].Id);
        Assert.NotNull(first.ContinuationToken);
        var byId = fixture.Features.Items.ToDictionary(feature => feature.RecordingId);
        Assert.All(first.Tracks.GroupBy(track => byId[track.Id].ArtistId), group => Assert.True(group.Count() <= RecommendationPolicy.MaximumPerArtist));
        Assert.All(first.Tracks.GroupBy(track => byId[track.Id].ReleaseId), group => Assert.True(group.Count() <= RecommendationPolicy.MaximumPerAlbum));
        var second = await fixture.Service.GenerateAsync(fixture.Owner.Id, new(RecommendationPolicy.SongRadio, ContinuationToken: first.ContinuationToken, Count: DesiredCount), CancellationToken.None);
        Assert.Empty(first.Tracks.Select(track => track.Id).Intersect(second.Tracks.Select(track => track.Id)));
        await Assert.ThrowsAsync<RecommendationInputException>(() => fixture.Service.GenerateAsync(Guid.NewGuid(),
            new(RecommendationPolicy.SongRadio, ContinuationToken: first.ContinuationToken), CancellationToken.None));
    }

    [PostgresFact]
    public async Task ExpiredRadioSessionReportsAStableRestartCode()
    {
        await using var fixture = await RecommendationFixture.CreateAsync();
        var first = await fixture.Service.GenerateAsync(fixture.Owner.Id, new(RecommendationPolicy.SongRadio, RecordingId: fixture.Features.Items[0].RecordingId, Count: DesiredCount), CancellationToken.None);
        fixture.Clock.Advance(RecommendationPolicy.SessionLifetime + TimeSpan.FromSeconds(1));
        var failure = await Assert.ThrowsAsync<RecommendationInputException>(() => fixture.Service.GenerateAsync(fixture.Owner.Id,
            new(RecommendationPolicy.SongRadio, ContinuationToken: first.ContinuationToken), CancellationToken.None));
        Assert.Equal(RecommendationPolicy.ExpiredSessionCode, failure.FailureCode);
    }

    [PostgresFact]
    public async Task SmartShuffleCannotReadForeignPlaylistAndStaysInsideOwnedPlaylist()
    {
        await using var fixture = await RecommendationFixture.CreateAsync();
        var identifiers = fixture.Features.Items.Take(DesiredCount).Select(feature => feature.RecordingId).ToArray();
        var playlist = new Playlist { Id = Guid.NewGuid(), OwnerId = fixture.Owner.Id, Name = TestName };
        fixture.Database.Playlists.Add(playlist);
        fixture.Database.PlaylistRecordings.AddRange(identifiers.Select((identifier, order) => new PlaylistRecording {
            PlaylistId = playlist.Id, RecordingId = identifier, Order = order, AddedById = fixture.Owner.Id }));
        await fixture.Database.SaveChangesAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.GenerateAsync(Guid.NewGuid(), new(RecommendationPolicy.SmartShuffle, PlaylistId: playlist.Id), CancellationToken.None));
        var result = await fixture.Service.GenerateAsync(fixture.Owner.Id, new(RecommendationPolicy.SmartShuffle, PlaylistId: playlist.Id), CancellationToken.None);
        Assert.Equal(identifiers.Order(), result.Tracks.Select(track => track.Id).Order());
    }

    [PostgresFact]
    public async Task DailyMixIsStableUntilExplicitRegenerationAndSavedPlaylistKeepsItsTracks()
    {
        await using var fixture = await RecommendationFixture.CreateAsync();
        var first = await fixture.Service.DailyAsync(fixture.Owner.Id, null, CancellationToken.None);
        var second = await fixture.Service.DailyAsync(fixture.Owner.Id, null, CancellationToken.None);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Tracks.Select(track => track.Id), second.Tracks.Select(track => track.Id));
        var saved = await fixture.Service.SaveAsync(fixture.Owner.Id, new(TestName, first.Tracks.Select(track => track.Id).ToArray(), first.Version), CancellationToken.None);
        var savedAgain = await fixture.Service.SaveAsync(fixture.Owner.Id, new(TestName, first.Tracks.Select(track => track.Id).ToArray(), first.Version), CancellationToken.None);
        Assert.Equal(saved.Id, savedAgain.Id);
        fixture.Database.ChangeTracker.Clear();
        var regenerated = await fixture.Service.DailyAsync(fixture.Owner.Id, first.Version, CancellationToken.None);
        Assert.NotEqual(first.Version, regenerated.Version);
        Assert.Equal(first.Tracks.Select(track => track.Id), await fixture.Database.PlaylistRecordings.Where(row => row.PlaylistId == saved.Id)
            .OrderBy(row => row.Order).Select(row => row.RecordingId).ToArrayAsync());
        await Assert.ThrowsAsync<RecommendationConflictException>(() => fixture.Service.DailyAsync(fixture.Owner.Id, first.Version, CancellationToken.None));
    }

    [PostgresFact]
    public async Task MissingPhysicalAudioDoesNotEnterRecommendationsAndBadInputsAreRejected()
    {
        await using var fixture = await RecommendationFixture.CreateAsync();
        foreach (var path in Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories)) File.Delete(path);
        var result = await fixture.Service.GenerateAsync(fixture.Owner.Id, new(RecommendationPolicy.Energy), CancellationToken.None);
        Assert.Empty(result.Tracks);
        await Assert.ThrowsAsync<RecommendationInputException>(() => fixture.Service.GenerateAsync(fixture.Owner.Id, new("unsupported-mode"), CancellationToken.None));
        await Assert.ThrowsAsync<RecommendationInputException>(() => fixture.Service.GenerateAsync(fixture.Owner.Id, new(RecommendationPolicy.SongRadio), CancellationToken.None));
    }

    [PostgresFact]
    public async Task FeatureCacheReusesWarmSnapshotAndRefreshesNewDeletedAndInvalidRows()
    {
        await using var fixture = await RecommendationFixture.CreateAsync();
        var first = fixture.Features.Items[0];
        var second = fixture.Features.Items[1];
        RecordingAudioAnalysis Analysis(AudioRecommendationFeature feature) => new()
        {
            RecordingId = feature.RecordingId, AudioSha256 = feature.ContentHash, SourceVersion = feature.ContentHash,
            EngineName = "AudioMuse-AI", EngineVersion = RecommendationPolicy.SupportedEngine,
            FeatureSchemaVersion = RecommendationPolicy.SupportedFeatureSchemaVersion, ProtocolVersion = RecommendationPolicy.PolicyVersion,
            ModelsJson = "[{\"name\":\"embedding\",\"sha256\":\"fixture-sha\"}]",
            EmbeddingJson = JsonSerializer.Serialize(feature.Vector), FeaturesJson = "{\"moods\":{\"chill\":0.55}}",
            DurationSeconds = 120, AnalyzedAt = fixture.Clock.GetUtcNow()
        };
        fixture.Database.RecordingAudioAnalyses.Add(Analysis(first));
        await fixture.Database.SaveChangesAsync();
        var cache = new AudioRecommendationFeatures(fixture.Scopes, fixture.Clock);
        var initial = await cache.GetAsync(CancellationToken.None);
        Assert.Single(initial.Features);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Database.RecordingAudioAnalyses.Add(Analysis(second));
        await fixture.Database.SaveChangesAsync();
        Assert.Same(initial, await cache.GetAsync(CancellationToken.None));
        fixture.Clock.Advance(RecommendationPolicy.CacheLifetime);
        var refreshed = await cache.GetAsync(CancellationToken.None);
        Assert.Equal(2, refreshed.Features.Count);
        Assert.NotEqual(initial.Generation, refreshed.Generation);
        await fixture.Database.RecordingAudioAnalyses.Where(analysis => analysis.RecordingId == first.RecordingId).ExecuteDeleteAsync();
        fixture.Clock.Advance(RecommendationPolicy.CacheLifetime);
        Assert.Single((await cache.GetAsync(CancellationToken.None)).Features);
        await fixture.Database.RecordingAudioAnalyses.Where(analysis => analysis.RecordingId == second.RecordingId)
            .ExecuteUpdateAsync(update => update.SetProperty(analysis => analysis.EmbeddingJson, "[]")
                .SetProperty(analysis => analysis.AnalyzedAt, fixture.Clock.GetUtcNow()));
        fixture.Clock.Advance(RecommendationPolicy.CacheLifetime);
        Assert.Empty((await cache.GetAsync(CancellationToken.None)).Features);
    }

    [PostgresFact]
    public async Task DailyMixMigrationAppliesWithoutAlteringIndexedRecordings()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: true);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        Assert.Empty(await database.RecommendationDailyMixes.AsNoTracking().ToArrayAsync());
        Assert.Empty(await database.Recordings.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task MissingIdentityIsRejectedBeforeRecommendationServicesAreCalled()
    {
        var controller = new AudioRecommendationsController(null!, null!, null!) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedObjectResult>(await controller.Overview(CancellationToken.None));
    }

    [PostgresFact]
    public async Task StaleTrackedDailyMixCannotCreateASecondSavedPlaylist()
    {
        await using var fixture = await RecommendationFixture.CreateAsync();
        var mix = await fixture.Service.DailyAsync(fixture.Owner.Id, null, CancellationToken.None);
        using var otherScope = fixture.Scopes.CreateScope();
        var other = fixture.ServiceFor(otherScope.ServiceProvider.GetRequiredService<StreamingDbContext>());
        var request = new SaveRecommendationPlaylist(TestName, mix.Tracks.Select(track => track.Id).ToArray(), mix.Version);
        var first = await other.SaveAsync(fixture.Owner.Id, request, CancellationToken.None);
        var repeated = await fixture.Service.SaveAsync(fixture.Owner.Id, request, CancellationToken.None);
        Assert.Equal(first.Id, repeated.Id);
        Assert.Single(await fixture.Database.Playlists.AsNoTracking().Where(playlist => playlist.OwnerId == fixture.Owner.Id).ToArrayAsync());
    }

    [PostgresFact]
    public async Task ConcurrentDailyRegenerationHasOneWinnerAndConcurrentSaveCreatesOnePlaylist()
    {
        await using var fixture = await RecommendationFixture.CreateAsync();
        var mix = await fixture.Service.DailyAsync(fixture.Owner.Id, null, CancellationToken.None);
        using var otherScope = fixture.Scopes.CreateScope();
        var other = fixture.ServiceFor(otherScope.ServiceProvider.GetRequiredService<StreamingDbContext>());
        var request = new SaveRecommendationPlaylist(TestName, mix.Tracks.Select(track => track.Id).ToArray(), mix.Version);
        var saved = await Task.WhenAll(fixture.Service.SaveAsync(fixture.Owner.Id, request, CancellationToken.None), other.SaveAsync(fixture.Owner.Id, request, CancellationToken.None));
        Assert.Equal(saved[0].Id, saved[1].Id);
        Assert.Single(await fixture.Database.Playlists.AsNoTracking().Where(playlist => playlist.OwnerId == fixture.Owner.Id).ToArrayAsync());
        fixture.Database.ChangeTracker.Clear();
        async Task<DailyMixResponse?> Regenerate(AudioRecommendations service)
        {
            try { return await service.DailyAsync(fixture.Owner.Id, mix.Version, CancellationToken.None); }
            catch (RecommendationConflictException) { return null; }
        }
        var regenerated = await Task.WhenAll(Regenerate(fixture.Service), Regenerate(other));
        Assert.Single(regenerated, result => result is not null);
        Assert.Single(regenerated, result => result is null);
    }

    private static AudioRecommendationFeature Feature() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(), Cohort,
        RecommendationRanking.Normalize(Enumerable.Repeat(1f, RecommendationPolicy.EmbeddingDimensions).ToArray())!, new Dictionary<string, double> { [Descriptor] = 0.5 }, 120, 0.5);

    private sealed class FeatureSource : IAudioRecommendationFeatures
    {
        public List<AudioRecommendationFeature> Items { get; } = [];
        public Task<AudioRecommendationSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new AudioRecommendationSnapshot(Cohort, Items));
    }
    private sealed class FixtureImages : IImageService
    {
        public string GetPreferredCover(string? local, string? optimized, string? original) => local ?? original ?? string.Empty;
        public AlbumCoversDto GetAlbumCover(Guid releaseId, Guid? releaseGroupId = null) => new();
        public AlbumCoversDto GetAllAlbumCovers(Guid releaseGroupId) => new();
    }
    private sealed class RecommendationFixture : IAsyncDisposable
    {
        private readonly DatabaseFixture fixture;
        private readonly IServiceScope scope;
        public StreamingDbContext Database { get; }
        public TestClock Clock => fixture.Clock;
        public IServiceScopeFactory Scopes => fixture.Services.GetRequiredService<IServiceScopeFactory>();
        public User Owner { get; }
        public FeatureSource Features { get; } = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        public AudioRecommendations Service { get; }
        private RecommendationFixture(DatabaseFixture fixture)
        {
            this.fixture = fixture;
            scope = fixture.Services.CreateScope();
            Database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            Owner = new User { Id = Guid.NewGuid(), Username = TestName, Email = Guid.NewGuid() + "@test.invalid", PasswordHash = FixtureCredential, ProviderId = FixtureCredential };
            Service = ServiceFor(Database);
        }
        public AudioRecommendations ServiceFor(StreamingDbContext database)
        {
            var images = new FixtureImages();
            var library = new LibraryService(database, null!, images);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [MusicPath] = Root }).Build();
            return new AudioRecommendations(database, Features, new(), new RecommendationCursorCodec(new EphemeralDataProtectionProvider()), fixture.Clock, images, library, configuration, new SystemRecommendationEntropy());
        }
        public static async Task<RecommendationFixture> CreateAsync()
        {
            var result = new RecommendationFixture(await DatabaseFixture.CreateAsync());
            Directory.CreateDirectory(Path.Combine(result.Root, TemporaryFolder));
            result.Database.Users.Add(result.Owner);
            for (var index = 0; index < CandidateCount; index++)
            {
                var feature = Feature();
                result.Features.Items.Add(feature);
                var artist = new Artist { Id = feature.ArtistId, Name = TestName, SortName = TestName };
                var release = new Release { Id = feature.ReleaseId, Title = TestName, Cover = string.Empty, Artist = artist };
                result.Database.Recordings.Add(new Recording { Id = feature.RecordingId, Title = TestName, Cover = string.Empty, Release = release });
                await File.WriteAllBytesAsync(Path.Combine(result.Root, TemporaryFolder, feature.RecordingId + AudioExtension), [1]);
            }
            await result.Database.SaveChangesAsync();
            return result;
        }
        public async ValueTask DisposeAsync() { scope.Dispose(); Directory.Delete(Root, true); await fixture.DisposeAsync(); }
    }
}
