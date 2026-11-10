using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Background;
using StreamingServiceServer.Business.AudioAnalysis;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class AudioAnalysisJobTests
{
    private const int NoCalls = 0;
    private const int OneCall = 1;
    private const int RecoveredAttempts = 2;
    private const int LargeAttemptCount = 100;
    private const string SourceIdentity = "test-source-sha";
    private const string ChangedSourceIdentity = "changed-test-source-sha";
    private const string EngineVersion = "test-engine-v1";
    private const string TestExtension = ".flac";
    private const string TestEndpoint = "http://localhost:8000/";
    private const string ExpectedUnavailableCode = "audio_unavailable";
    private const string ExpectedChangedSourceCode = "source_changed";
    private const string SafeRetryFailure = "provider_busy";
    private const string PrivateFailureText = "private filesystem path must never persist";
    private const string ExpectedGenericFailure = "analysis_failed";
    private const double TrackDurationSeconds = 120;
    private static readonly Guid RecordingIdentifier = Guid.Parse("93c08e64-7e6e-40f8-98d0-6546551dc174");
    private static readonly Guid SettingsRevision = Guid.Parse("6ff4b868-1624-4014-a1c1-098752784263");
    private static readonly Guid ChangedSettingsRevision = Guid.Parse("168c2b56-eb5a-41c1-8f32-cee33ca63634");
    private static readonly AudioAnalysisConfiguration EnabledConfiguration = new(true, TestEndpoint, string.Empty, AudioAnalysisProtocol.Profile, SettingsRevision);

    [Fact]
    public async Task DisabledProcessingDoesNotDiscoverClaimOrOpenAudio()
    {
        using var fixture = new WorkerFixture();
        fixture.Settings.Configuration = EnabledConfiguration with { Enabled = false };
        Assert.False(await fixture.Worker.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(NoCalls, fixture.Store.DiscoverCalls);
        Assert.Equal(NoCalls, fixture.Store.ClaimCalls);
        Assert.Equal(NoCalls, fixture.Source.OpenCalls);
        Assert.Equal(NoCalls, fixture.Client.AnalysisCalls);
    }

    [Fact]
    public async Task SuccessfulAnalysisRechecksAudioAndPersistsOnlyCurrentLease()
    {
        using var fixture = new WorkerFixture();
        Assert.True(await fixture.Worker.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(OneCall, fixture.Client.AnalysisCalls);
        Assert.Equal(RecoveredAttempts, fixture.Source.OpenCalls);
        Assert.Equal(OneCall, fixture.Store.CompletedCalls);
        Assert.Equal(fixture.Store.Lease.Token, fixture.Client.RequestIdentifier);
        Assert.Empty(fixture.Store.Failures);
    }

    [Fact]
    public async Task UnchangedAudioAndProfileReuseStoredAnalysisWithoutProviderRequest()
    {
        using var fixture = new WorkerFixture();
        fixture.Store.Current = CreateAnalysis();
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(NoCalls, fixture.Client.AnalysisCalls);
        Assert.Equal(OneCall, fixture.Store.CompletedCalls);
    }

    [Fact]
    public async Task ChangedSettingsAfterResponseDiscardResultAndReleaseLease()
    {
        using var fixture = new WorkerFixture();
        fixture.Client.BeforeResult = () => fixture.Settings.Configuration = EnabledConfiguration with { Revision = ChangedSettingsRevision };
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(NoCalls, fixture.Store.CompletedCalls);
        Assert.Equal(OneCall, fixture.Store.ReleasedCalls);
    }

    [Fact]
    public async Task DisabledSettingsAfterResponseDiscardResultAndReleaseLease()
    {
        using var fixture = new WorkerFixture();
        fixture.Client.BeforeResult = () => fixture.Settings.Configuration = EnabledConfiguration with { Enabled = false };
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(NoCalls, fixture.Store.CompletedCalls);
        Assert.Equal(OneCall, fixture.Store.ReleasedCalls);
    }

    [Fact]
    public async Task ChangedAudioDuringAnalysisCannotReplacePreviousGoodResult()
    {
        using var fixture = new WorkerFixture();
        fixture.Client.BeforeResult = () => fixture.Source.Identity = ChangedSourceIdentity;
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(NoCalls, fixture.Store.CompletedCalls);
        Assert.Equal(ExpectedChangedSourceCode, Assert.Single(fixture.Store.Failures).Code);
    }

    [Fact]
    public async Task MissingAudioIsDeferredWithoutCallingProvider()
    {
        using var fixture = new WorkerFixture();
        fixture.Source.Missing = true;
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(NoCalls, fixture.Client.AnalysisCalls);
        var failure = Assert.Single(fixture.Store.Failures);
        Assert.Equal(ExpectedUnavailableCode, failure.Code);
        Assert.True(failure.Unavailable);
        Assert.Equal(fixture.Clock.GetUtcNow() + AudioAnalysisJobStore.SourceRecheckInterval, failure.NextAttempt);
    }

    [Fact]
    public async Task FailurePersistsOnlySafeCodeAndBackoff()
    {
        using var fixture = new WorkerFixture();
        fixture.Client.Failure = new InvalidOperationException(PrivateFailureText);
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        var failure = Assert.Single(fixture.Store.Failures);
        Assert.Equal(ExpectedGenericFailure, failure.Code);
        Assert.Equal(fixture.Clock.GetUtcNow() + fixture.Options.InitialRetryDelay, failure.NextAttempt);
    }

    [Fact]
    public async Task RejectedCommitReleasesLeaseWithoutReportingCompletion()
    {
        using var fixture = new WorkerFixture();
        fixture.Store.AcceptCompletion = false;
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(OneCall, fixture.Store.ReleasedCalls);
    }

    [Fact]
    public async Task ProviderFailureStopsAdmissionUntilGlobalCooldownExpires()
    {
        using var fixture = new WorkerFixture();
        fixture.Client.Failure = new AudioAnalysisException(SafeRetryFailure);
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.False(await fixture.Worker.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(OneCall, fixture.Store.ClaimCalls);
        Assert.Equal(OneCall, fixture.Client.AnalysisCalls);
        fixture.Clock.Advance(fixture.Options.ProviderFailureCooldown);
        fixture.Client.Failure = null;
        Assert.True(await fixture.Worker.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(RecoveredAttempts, fixture.Client.AnalysisCalls);
    }

    [Fact]
    public async Task SettingsMonitorCancelsInFlightRequestWhenProcessingIsDisabled()
    {
        using var fixture = new WorkerFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Analyze = async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResult();
        };
        var processing = fixture.Worker.ProcessNextAsync(CancellationToken.None);
        await started.Task;
        fixture.Settings.Configuration = EnabledConfiguration with { Enabled = false };
        fixture.Clock.Advance(fixture.Options.SettingsCheckInterval);
        await processing;
        Assert.Equal(NoCalls, fixture.Store.CompletedCalls);
        Assert.Equal(OneCall, fixture.Store.ReleasedCalls);
        Assert.Empty(fixture.Store.Failures);
    }

    [Fact]
    public async Task ShutdownCancellationReleasesLeaseWithoutPersistingFailure()
    {
        using var fixture = new WorkerFixture();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Analyze = async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResult();
        };
        var processing = fixture.Worker.ProcessNextAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.Equal(OneCall, fixture.Store.ReleasedCalls);
        Assert.Empty(fixture.Store.Failures);
    }

    [Fact]
    public void RetryDelayIsBoundedAndPreservesFirstAttemptDelay()
    {
        var options = new AudioAnalysisJobOptions();
        Assert.Equal(options.InitialRetryDelay, AudioAnalysisJobService.RetryDelay(OneCall, options));
        Assert.Equal(options.MaximumRetryDelay, AudioAnalysisJobService.RetryDelay(LargeAttemptCount, options));
    }

    [PostgresFact]
    public async Task ConcurrentWorkersClaimOnlyOneLease()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var configuration = await EnableDatabaseAsync(fixture);
        var recordingIdentifier = await AddRecordingAsync(fixture);
        using (var discovery = fixture.Services.CreateScope())
            await CreateStore(discovery, fixture.Clock).DiscoverAsync(OneCall, CancellationToken.None);
        using var firstScope = fixture.Services.CreateScope();
        using var secondScope = fixture.Services.CreateScope();
        var leaseLifetime = new AudioAnalysisJobOptions().LeaseLifetime;
        var leases = await Task.WhenAll(CreateStore(firstScope, fixture.Clock).ClaimAsync(configuration, leaseLifetime, CancellationToken.None),
            CreateStore(secondScope, fixture.Clock).ClaimAsync(configuration, leaseLifetime, CancellationToken.None));
        Assert.Equal(recordingIdentifier, Assert.Single(leases.OfType<AudioAnalysisLease>()).RecordingId);
    }

    [PostgresFact]
    public async Task ExpiredLeaseCanBeRecoveredAndOldWorkerCannotPublishOrFailNewLease()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var configuration = await EnableDatabaseAsync(fixture);
        await AddRecordingAsync(fixture);
        var options = new AudioAnalysisJobOptions();
        AudioAnalysisLease oldLease;
        using (var initialScope = fixture.Services.CreateScope())
        {
            var initial = CreateStore(initialScope, fixture.Clock);
            await initial.DiscoverAsync(OneCall, CancellationToken.None);
            oldLease = (await initial.ClaimAsync(configuration, options.LeaseLifetime, CancellationToken.None))!;
        }
        fixture.Clock.Advance(options.LeaseLifetime);
        using var recoveryScope = fixture.Services.CreateScope();
        var recovered = CreateStore(recoveryScope, fixture.Clock);
        var newLease = (await recovered.ClaimAsync(configuration, options.LeaseLifetime, CancellationToken.None))!;
        Assert.NotEqual(oldLease.Token, newLease.Token);
        Assert.Equal(RecoveredAttempts, newLease.Attempts);
        await using var source = CreateSource();
        Assert.False(await recovered.CompleteAsync(oldLease, configuration, source, CreateResult(), CancellationToken.None));
        await recovered.FailAsync(oldLease, SafeRetryFailure, fixture.Clock.GetUtcNow(), false, CancellationToken.None);
        var database = recoveryScope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var job = await database.AudioAnalysisJobs.AsNoTracking().SingleAsync();
        Assert.Equal(newLease.Token, job.LeaseToken);
        Assert.Equal(AudioAnalysisJobState.Processing, job.State);
        Assert.Empty(await database.RecordingAudioAnalyses.ToArrayAsync());
    }

    [PostgresFact]
    public async Task DisabledOrReplacedSettingsPreventClaimAndLateResultPublication()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var configuration = await EnableDatabaseAsync(fixture);
        await AddRecordingAsync(fixture);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var store = CreateStore(scope, fixture.Clock);
        await store.DiscoverAsync(OneCall, CancellationToken.None);
        var lease = (await store.ClaimAsync(configuration, new AudioAnalysisJobOptions().LeaseLifetime, CancellationToken.None))!;
        await database.AudioAnalysisSettings.ExecuteUpdateAsync(update => update.SetProperty(settings => settings.Enabled, false)
            .SetProperty(settings => settings.Revision, ChangedSettingsRevision));
        await using var source = CreateSource();
        Assert.False(await store.CompleteAsync(lease, configuration, source, CreateResult(), CancellationToken.None));
        await store.ReleaseAsync(lease, CancellationToken.None);
        Assert.Null(await store.ClaimAsync(configuration, new AudioAnalysisJobOptions().LeaseLifetime, CancellationToken.None));
        Assert.Empty(await database.RecordingAudioAnalyses.ToArrayAsync());
    }

    [PostgresFact]
    public async Task RetryStateSurvivesStoreRecreationAndCompletedAnalysisRemainsSingleCurrentResult()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var configuration = await EnableDatabaseAsync(fixture);
        var recordingIdentifier = await AddRecordingAsync(fixture);
        var options = new AudioAnalysisJobOptions();
        using (var firstScope = fixture.Services.CreateScope())
        {
            var first = CreateStore(firstScope, fixture.Clock);
            await first.DiscoverAsync(OneCall, CancellationToken.None);
            var lease = (await first.ClaimAsync(configuration, options.LeaseLifetime, CancellationToken.None))!;
            await first.FailAsync(lease, SafeRetryFailure, fixture.Clock.GetUtcNow() + options.InitialRetryDelay, false, CancellationToken.None);
        }
        using var retryScope = fixture.Services.CreateScope();
        var retry = CreateStore(retryScope, fixture.Clock);
        Assert.Null(await retry.ClaimAsync(configuration, options.LeaseLifetime, CancellationToken.None));
        fixture.Clock.Advance(options.InitialRetryDelay);
        var retryLease = (await retry.ClaimAsync(configuration, options.LeaseLifetime, CancellationToken.None))!;
        Assert.Equal(RecoveredAttempts, retryLease.Attempts);
        await using var source = CreateSource();
        Assert.True(await retry.CompleteAsync(retryLease, configuration, source, CreateResult(), CancellationToken.None));
        var analysis = await retry.GetCurrentAsync(recordingIdentifier, CancellationToken.None);
        Assert.NotNull(analysis);
        Assert.Equal(SourceIdentity, analysis.AudioSha256);
        fixture.Clock.Advance(AudioAnalysisJobStore.SourceRecheckInterval);
        var recheckLease = (await retry.ClaimAsync(configuration, options.LeaseLifetime, CancellationToken.None))!;
        Assert.True(await retry.CompleteAsync(recheckLease, configuration, source, CreateResult(), CancellationToken.None));
        var database = retryScope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        Assert.Single(await database.RecordingAudioAnalyses.ToArrayAsync());
        Assert.Equal(analysis.AnalyzedAt, (await retry.GetCurrentAsync(recordingIdentifier, CancellationToken.None))!.AnalyzedAt);
    }

    [PostgresFact]
    public async Task ChangedSettingsRevisionWhileStillEnabledRejectsOldResult()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var configuration = await EnableDatabaseAsync(fixture);
        await AddRecordingAsync(fixture);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var store = CreateStore(scope, fixture.Clock);
        await store.DiscoverAsync(OneCall, CancellationToken.None);
        var lease = (await store.ClaimAsync(configuration, new AudioAnalysisJobOptions().LeaseLifetime, CancellationToken.None))!;
        await database.AudioAnalysisSettings.ExecuteUpdateAsync(update => update.SetProperty(settings => settings.Revision, ChangedSettingsRevision));
        await using var source = CreateSource();
        Assert.False(await store.CompleteAsync(lease, configuration, source, CreateResult(), CancellationToken.None));
        Assert.Empty(await database.RecordingAudioAnalyses.ToArrayAsync());
    }

    [PostgresFact]
    public async Task MigrationCreatesDisabledSettingsWithoutChangingRecordingData()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(true);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var settings = await database.AudioAnalysisSettings.SingleAsync();
        Assert.False(settings.Enabled);
        Assert.Equal(AudioAnalysisStoragePolicy.InitialSettingsRevision, settings.Revision);
        Assert.Empty(await database.AudioAnalysisJobs.ToArrayAsync());
        Assert.Empty(await database.RecordingAudioAnalyses.ToArrayAsync());
    }

    private static AudioAnalysisJobStore CreateStore(IServiceScope scope, TimeProvider clock) =>
        new(scope.ServiceProvider.GetRequiredService<StreamingDbContext>(), clock);

    private static async Task<AudioAnalysisConfiguration> EnableDatabaseAsync(DatabaseFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        await database.AudioAnalysisSettings.ExecuteUpdateAsync(update => update.SetProperty(settings => settings.Enabled, true)
            .SetProperty(settings => settings.Revision, SettingsRevision));
        return EnabledConfiguration;
    }

    private static async Task<Guid> AddRecordingAsync(DatabaseFixture fixture)
    {
        var releaseIdentifier = await fixture.AddReleaseAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var release = await database.Releases.SingleAsync(release => release.Id == releaseIdentifier);
        var recording = new Recording { Id = RecordingIdentifier, Title = SourceIdentity, Cover = string.Empty, Release = release };
        database.Recordings.Add(recording);
        await database.SaveChangesAsync();
        return recording.Id;
    }

    private static AudioAnalysisSource CreateSource(string identity = SourceIdentity) => new(new MemoryStream(), identity, identity, TestExtension);
    private static AudioAnalysisResult CreateResult() => new(AudioAnalysisProtocol.Version, AudioAnalysisProtocol.FeatureSchemaVersion,
        AudioAnalysisProtocol.EngineName, EngineVersion, AudioAnalysisProtocol.Profile, SourceIdentity,
        AudioAnalysisStoragePolicy.EmptyObjectJson, AudioAnalysisStoragePolicy.EmptyArrayJson, AudioAnalysisStoragePolicy.EmptyObjectJson,
        AudioAnalysisProtocol.EmbeddingDimensions, TrackDurationSeconds, null, null, null, null);
    private static RecordingAudioAnalysis CreateAnalysis() => new()
    {
        RecordingId = RecordingIdentifier, SourceVersion = SourceIdentity, AudioSha256 = SourceIdentity,
        Profile = AudioAnalysisProtocol.Profile, EngineName = AudioAnalysisProtocol.EngineName, EngineVersion = EngineVersion,
        ProtocolVersion = AudioAnalysisProtocol.Version, FeatureSchemaVersion = AudioAnalysisProtocol.FeatureSchemaVersion,
        EmbeddingDimensions = AudioAnalysisProtocol.EmbeddingDimensions, DurationSeconds = TrackDurationSeconds
    };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset current = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => current;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            current += elapsed;
            foreach (var timer in timers.ToArray()) timer.FireIfDue(current);
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset dueAt = DateTimeOffset.MaxValue;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                dueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime;
                return true;
            }
            public void FireIfDue(DateTimeOffset now)
            {
                if (disposed || dueAt > now) return;
                dueAt = DateTimeOffset.MaxValue;
                callback(state);
            }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class WorkerFixture : IDisposable
    {
        public MutableSettings Settings { get; } = new();
        public MemoryJobStore Store { get; } = new();
        public MemorySource Source { get; } = new();
        public MemoryClient Client { get; } = new();
        public ManualClock Clock { get; } = new();
        public AudioAnalysisJobOptions Options { get; } = new();
        private readonly ServiceProvider services;
        public AudioAnalysisJobService Worker { get; }

        public WorkerFixture()
        {
            services = new ServiceCollection().AddSingleton<IAudioAnalysisSettingsStore>(Settings)
                .AddSingleton<IAudioAnalysisJobStore>(Store).AddSingleton<IAudioAnalysisSource>(Source)
                .AddSingleton<IAudioAnalysisClient>(Client).BuildServiceProvider();
            Worker = new AudioAnalysisJobService(services.GetRequiredService<IServiceScopeFactory>(),
                Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<AudioAnalysisJobService>.Instance);
        }
        public void Dispose() { Worker.Dispose(); services.Dispose(); }
    }

    private sealed class MutableSettings : IAudioAnalysisSettingsStore
    {
        public AudioAnalysisConfiguration Configuration { get; set; } = EnabledConfiguration;
        public Task<AudioAnalysisConfiguration> GetConfigurationAsync(CancellationToken cancellationToken) => Task.FromResult(Configuration);
    }

    private sealed class MemorySource : IAudioAnalysisSource
    {
        public bool Missing { get; set; }
        public string Identity { get; set; } = SourceIdentity;
        public int OpenCalls { get; private set; }
        public Task<AudioAnalysisSource?> OpenAsync(Guid recordingId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCalls++;
            return Task.FromResult<AudioAnalysisSource?>(Missing ? null : CreateSource(Identity));
        }
    }

    private sealed class MemoryClient : IAudioAnalysisClient
    {
        public int AnalysisCalls { get; private set; }
        public Guid RequestIdentifier { get; private set; }
        public Action? BeforeResult { get; set; }
        public Exception? Failure { get; set; }
        public Func<CancellationToken, Task<AudioAnalysisResult>>? Analyze { get; set; }
        public Task<bool> CheckHealthAsync(AudioAnalysisConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<AudioAnalysisResult> AnalyzeAsync(AudioAnalysisSource source, Guid requestId, AudioAnalysisConfiguration configuration, CancellationToken cancellationToken)
        {
            AnalysisCalls++;
            RequestIdentifier = requestId;
            BeforeResult?.Invoke();
            if (Analyze is not null) return Analyze(cancellationToken);
            if (Failure is not null) throw Failure;
            return Task.FromResult(CreateResult());
        }
    }

    private sealed record FailedJob(string Code, DateTimeOffset NextAttempt, bool Unavailable);
    private sealed class MemoryJobStore : IAudioAnalysisJobStore
    {
        public AudioAnalysisLease Lease { get; } = new(RecordingIdentifier, SettingsRevision, ChangedSettingsRevision, OneCall);
        public RecordingAudioAnalysis? Current { get; set; }
        public bool AcceptCompletion { get; set; } = true;
        public int DiscoverCalls { get; private set; }
        public int ClaimCalls { get; private set; }
        public int CompletedCalls { get; private set; }
        public int ReleasedCalls { get; private set; }
        public List<FailedJob> Failures { get; } = [];
        public Task DiscoverAsync(int batchSize, CancellationToken cancellationToken) { DiscoverCalls++; return Task.CompletedTask; }
        public Task<AudioAnalysisLease?> ClaimAsync(AudioAnalysisConfiguration configuration, TimeSpan leaseLifetime, CancellationToken cancellationToken)
        { ClaimCalls++; return Task.FromResult<AudioAnalysisLease?>(Lease); }
        public Task<RecordingAudioAnalysis?> GetCurrentAsync(Guid recordingIdentifier, CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task<bool> CompleteAsync(AudioAnalysisLease lease, AudioAnalysisConfiguration configuration, AudioAnalysisSource source, AudioAnalysisResult result, CancellationToken cancellationToken)
        { CompletedCalls++; return Task.FromResult(AcceptCompletion); }
        public Task FailAsync(AudioAnalysisLease lease, string safeCode, DateTimeOffset nextAttemptAt, bool unavailable, CancellationToken cancellationToken)
        { Failures.Add(new FailedJob(safeCode, nextAttemptAt, unavailable)); return Task.CompletedTask; }
        public Task ReleaseAsync(AudioAnalysisLease lease, CancellationToken cancellationToken) { ReleasedCalls++; return Task.CompletedTask; }
    }
}
