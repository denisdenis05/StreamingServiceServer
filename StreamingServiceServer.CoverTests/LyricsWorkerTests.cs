using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Background;
using StreamingServiceServer.Business.Lyrics;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class LyricsWorkerTests
{
    [Fact]
    public async Task DisabledWorkerDoesNotDiscoverClaimOrDownload()
    {
        using var fixture = new WorkerFixture();
        fixture.Options.Enabled = false;
        Assert.False(await fixture.Worker.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(LyricsTestData.NoCalls, fixture.Store.DiscoverCalls);
        Assert.Equal(LyricsTestData.NoCalls, fixture.Provider.Calls);
    }

    [Fact]
    public async Task MatchingLyricsAreDownloadedStoredAndReleasedWithoutBlockingPlayback()
    {
        using var fixture = new WorkerFixture();
        Assert.True(await fixture.Worker.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(LyricsDownloadState.Downloaded, fixture.Store.CompletedState);
        Assert.Equal(LyricsTestData.Content, fixture.Store.Content);
        Assert.Equal(LyricsTestData.OneCall, fixture.Provider.Calls);
        Assert.Equal(LyricsTestData.TwoCalls, fixture.Catalog.ReadCalls);
        Assert.Equal(fixture.Clock.GetUtcNow() + fixture.Options.MetadataRecheckInterval, fixture.Store.NextAttemptAt);
        Assert.Equal(LyricsTestData.OneCall, fixture.Store.ReleaseCalls);
    }

    [Fact]
    public async Task DashboardNamingFlagSkipsDownloadAndClearsAnyPreviousContent()
    {
        using var fixture = new WorkerFixture();
        fixture.Catalog.Track = LyricsTestData.Track with { HasNamingIssue = true };
        fixture.Store.Job.State = LyricsDownloadState.Downloaded;
        fixture.Store.Job.SourceRevision = fixture.Catalog.Track.Revision;
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsDownloadState.Skipped, fixture.Store.CompletedState);
        Assert.Equal(LyricsProtocol.NamingIssue, fixture.Store.FailureCode);
        Assert.Null(fixture.Store.Content);
        Assert.Equal(LyricsTestData.NoCalls, fixture.Provider.Calls);
    }

    [Fact]
    public async Task MissingArtistOrDurationSkipsWithoutNetworkAccess()
    {
        using var fixture = new WorkerFixture();
        fixture.Catalog.Track = LyricsTestData.Track with { Artist = string.Empty, DurationMilliseconds = null };
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsProtocol.InvalidMetadata, fixture.Store.FailureCode);
        Assert.Equal(LyricsDownloadState.Skipped, fixture.Store.CompletedState);
        Assert.Equal(LyricsTestData.NoCalls, fixture.Provider.Calls);
    }

    [Fact]
    public async Task ExistingLocalLyricsAreReusedWithoutAnExternalConnection()
    {
        using var fixture = new WorkerFixture();
        fixture.Store.Job.State = LyricsDownloadState.Downloaded;
        fixture.Store.Job.SourceRevision = LyricsTestData.Track.Revision;
        fixture.Store.Job.ProviderIdentifier = LyricsTestData.ProviderIdentifier;
        fixture.Store.Job.PlainLyrics = LyricsTestData.Plain;
        fixture.Store.Job.SyncedLyrics = LyricsTestData.Synced;
        fixture.Store.Job.Lyricsfile = LyricsTestData.Lyricsfile;
        fixture.Provider.Failure = new HttpRequestException(LyricsTestData.PrivateFailure);
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsTestData.NoCalls, fixture.Provider.Calls);
        Assert.Equal(LyricsTestData.Content, fixture.Store.Content);
        Assert.Equal(LyricsDownloadState.Downloaded, fixture.Store.CompletedState);
    }

    [Fact]
    public async Task NewFlagDuringDownloadDiscardsTheResponse()
    {
        using var fixture = new WorkerFixture();
        fixture.Provider.BeforeReturn = () => fixture.Catalog.Track = LyricsTestData.Track with { HasNamingIssue = true };
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Null(fixture.Store.CompletedState);
        Assert.Equal(LyricsProtocol.ChangedMetadata, fixture.Store.FailureCode);
        Assert.Null(fixture.Store.Content);
    }

    [Fact]
    public async Task ChangedTitleDuringDownloadDiscardsTheResponse()
    {
        using var fixture = new WorkerFixture();
        fixture.Provider.BeforeReturn = () => fixture.Catalog.Track = LyricsTestData.Track with { Title = LyricsTestData.OtherTitle };
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Null(fixture.Store.CompletedState);
        Assert.Equal(LyricsProtocol.ChangedMetadata, fixture.Store.FailureCode);
    }

    [Fact]
    public async Task MissingLyricsHaveAPersistedLongRetryInterval()
    {
        using var fixture = new WorkerFixture();
        fixture.Provider.Content = null;
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsDownloadState.NotFound, fixture.Store.CompletedState);
        Assert.Equal(fixture.Clock.GetUtcNow() + fixture.Options.NotFoundRetryInterval, fixture.Store.NextAttemptAt);
    }

    [Fact]
    public async Task InstrumentalResultDoesNotCauseRepeatedMissingLyricsDownloads()
    {
        using var fixture = new WorkerFixture();
        fixture.Provider.Content = LyricsTestData.Content with { Instrumental = true, PlainLyrics = null, SyncedLyrics = null };
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsDownloadState.Instrumental, fixture.Store.CompletedState);
    }

    [Fact]
    public async Task UnmatchedRecordingIsSkippedRatherThanRetriedAsATransientFailure()
    {
        using var fixture = new WorkerFixture();
        fixture.Provider.Failure = new LyricsProviderException(LyricsProtocol.UnmatchedRecording);
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsDownloadState.Skipped, fixture.Store.CompletedState);
        Assert.Equal(LyricsProtocol.UnmatchedRecording, fixture.Store.FailureCode);
    }

    [Fact]
    public async Task TransportFailureStoresOnlyASafeCodeAndBackoff()
    {
        using var fixture = new WorkerFixture();
        fixture.Provider.Failure = new HttpRequestException(LyricsTestData.PrivateFailure);
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsProtocol.ProviderFailure, fixture.Store.FailureCode);
        Assert.Equal(fixture.Clock.GetUtcNow() + fixture.Options.InitialRetryDelay, fixture.Store.NextAttemptAt);
        Assert.Equal(fixture.Store.NextAttemptAt, fixture.Store.NextRequestAt);
    }

    [Fact]
    public async Task RateLimitCooldownDelaysTheWholeQueueRatherThanOnlyTheFailedSong()
    {
        using var fixture = new WorkerFixture();
        fixture.Provider.Failure = new LyricsProviderException(LyricsProtocol.ProviderFailure, LyricsTestData.ProviderCooldown);
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(fixture.Clock.GetUtcNow() + LyricsTestData.ProviderCooldown, fixture.Store.NextRequestAt);
        Assert.Equal(fixture.Store.NextRequestAt, fixture.Store.NextAttemptAt);
    }

    [Fact]
    public async Task ShutdownCancelsDownloadAndReleasesItsLeaseWithoutAStoredFailure()
    {
        using var fixture = new WorkerFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Provider.BeforeReturn = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Worker.ProcessNextAsync(cancellation.Token));
        Assert.Null(fixture.Store.FailureCode);
        Assert.Equal(LyricsTestData.OneCall, fixture.Store.ReleaseCalls);
    }

    [Fact]
    public async Task OperationDeadlineCancelsDownloadAndStoresATimeoutRetry()
    {
        using var fixture = new WorkerFixture();
        fixture.Provider.BeforeReturn = () => fixture.Clock.Advance(fixture.Options.OperationTimeout);
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsProtocol.ProviderTimeout, fixture.Store.FailureCode);
        Assert.Equal(fixture.Clock.GetUtcNow() + fixture.Options.InitialRetryDelay, fixture.Store.NextRequestAt);
    }

    [Fact]
    public async Task ConcurrentProcessingCallsCannotDownloadTheSameJobTwice()
    {
        using var fixture = new WorkerFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BeforeReturn = () => started.SetResult();
        fixture.Provider.AwaitReturn = cancellationToken => released.Task.WaitAsync(cancellationToken);
        var first = fixture.Worker.ProcessNextAsync(CancellationToken.None);
        await started.Task;
        var second = fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsTestData.OneCall, fixture.Provider.Calls);
        released.SetResult();
        Assert.True(await first);
        Assert.False(await second);
        Assert.Equal(LyricsTestData.OneCall, fixture.Provider.Calls);
    }

    [Fact]
    public async Task EmptyQueueDiscoveryIsThrottledUntilItsConfiguredInterval()
    {
        using var fixture = new WorkerFixture();
        fixture.Store.Available = false;
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsTestData.OneCall, fixture.Store.DiscoverCalls);
        fixture.Clock.Advance(fixture.Options.DiscoveryInterval);
        await fixture.Worker.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(LyricsTestData.TwoCalls, fixture.Store.DiscoverCalls);
    }

    [Fact]
    public async Task NoDueJobDoesNotCallTheProvider()
    {
        using var fixture = new WorkerFixture();
        fixture.Store.Available = false;
        Assert.False(await fixture.Worker.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(LyricsTestData.NoCalls, fixture.Provider.Calls);
    }

    [Fact]
    public void RetryDelayIsBoundedForInitialNegativeAndVeryLargeAttemptCounts()
    {
        var options = new LyricsOptions();
        Assert.Equal(options.InitialRetryDelay, LyricsDownloadJobService.RetryDelay(LyricsTestData.NoCalls, options));
        Assert.Equal(options.InitialRetryDelay, LyricsDownloadJobService.RetryDelay(-LyricsTestData.OneCall, options));
        Assert.Equal(options.MaximumRetryDelay, LyricsDownloadJobService.RetryDelay(LyricsTestData.ManyAttempts, options));
    }

    private sealed class WorkerFixture : IDisposable
    {
        public ManualLyricsClock Clock { get; } = new();
        public LyricsOptions Options { get; } = new();
        public FakeStore Store { get; } = new();
        public FakeCatalog Catalog { get; } = new();
        public FakeProvider Provider { get; } = new();
        private readonly ServiceProvider services;
        public LyricsDownloadJobService Worker { get; }
        public WorkerFixture()
        {
            var collection = new ServiceCollection();
            collection.AddSingleton<ILyricsJobStore>(Store);
            collection.AddSingleton<ILyricsCatalog>(Catalog);
            collection.AddSingleton<ILyricsProvider>(Provider);
            services = collection.BuildServiceProvider();
            Worker = new LyricsDownloadJobService(services.GetRequiredService<IServiceScopeFactory>(),
                Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<LyricsDownloadJobService>.Instance);
        }
        public void Dispose() { Worker.Dispose(); services.Dispose(); }
    }

    private sealed class ManualLyricsClock : TimeProvider
    {
        private const string InitialTimestamp = "2026-11-20T00:00:00Z";
        private DateTimeOffset now = DateTimeOffset.Parse(InitialTimestamp, CultureInfo.InvariantCulture);
        private readonly List<ManualLyricsTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualLyricsTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            now += duration;
            foreach (var timer in timers.ToArray()) timer.FireIfDue();
        }
        private sealed class ManualLyricsTimer(ManualLyricsClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? dueAt;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                return true;
            }
            public void FireIfDue()
            {
                if (disposed || dueAt is null || dueAt > clock.GetUtcNow()) return;
                dueAt = null;
                callback(state);
            }
            public void Dispose() { disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeCatalog : ILyricsCatalog
    {
        public LyricsTrack? Track { get; set; } = LyricsTestData.Track;
        public int ReadCalls { get; private set; }
        public Task<LyricsTrack?> ReadAsync(Guid recordingIdentifier, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls += LyricsTestData.OneCall;
            return Task.FromResult(Track);
        }
    }

    private sealed class FakeProvider : ILyricsProvider
    {
        public LyricsContent? Content { get; set; } = LyricsTestData.Content;
        public Exception? Failure { get; set; }
        public Action? BeforeReturn { get; set; }
        public Func<CancellationToken, Task>? AwaitReturn { get; set; }
        public int Calls { get; private set; }
        public async Task<LyricsContent?> DownloadAsync(LyricsTrack track, CancellationToken cancellationToken)
        {
            Calls += LyricsTestData.OneCall;
            BeforeReturn?.Invoke();
            if (AwaitReturn is not null) await AwaitReturn(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return Content;
        }
    }

    private sealed class FakeStore : ILyricsJobStore
    {
        public RecordingLyrics Job { get; } = new() { RecordingIdentifier = LyricsTestData.RecordingIdentifier };
        public bool Available { get; set; } = true;
        public int DiscoverCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public LyricsDownloadState? CompletedState { get; private set; }
        public LyricsContent? Content { get; private set; }
        public string? FailureCode { get; private set; }
        public DateTimeOffset NextAttemptAt { get; private set; }
        public DateTimeOffset NextRequestAt { get; private set; }
        public Task DiscoverAsync(int batchSize, CancellationToken cancellationToken) { DiscoverCalls += LyricsTestData.OneCall; return Task.CompletedTask; }
        public Task<LyricsLease?> ClaimAsync(TimeSpan leaseLifetime, CancellationToken cancellationToken) =>
            Task.FromResult(Available ? new LyricsLease(Job, LyricsTestData.FirstToken) : null);
        public Task CompleteAsync(LyricsLease lease, LyricsTrack track, LyricsDownloadState state, LyricsContent? content,
            string failureCode, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
        {
            Available = false; CompletedState = state; Content = content; FailureCode = failureCode; NextAttemptAt = nextAttemptAt;
            return Task.CompletedTask;
        }
        public Task FailAsync(LyricsLease lease, string failureCode, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
        { FailureCode = failureCode; NextAttemptAt = nextAttemptAt; return Task.CompletedTask; }
        public Task ReleaseAsync(LyricsLease lease, DateTimeOffset nextRequestAt, CancellationToken cancellationToken)
        { ReleaseCalls += LyricsTestData.OneCall; NextRequestAt = nextRequestAt; return Task.CompletedTask; }
    }
}
