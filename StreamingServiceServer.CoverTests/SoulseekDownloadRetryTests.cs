using System.Net;
using System.Text;
using System.Text.Json;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Models.Slskd;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class SoulseekDownloadRetryTests
{
    private const string Username = "album-owner";
    private const string DirectoryPath = "Music/Album";
    private const long TrackSizeBytes = 1_000_000;
    private static readonly SlskdFileDownloadRequest FirstTrack = new()
        { Filename = DirectoryPath + "/01.flac", Size = TrackSizeBytes };
    private static readonly SlskdFileDownloadRequest SecondTrack = new()
        { Filename = DirectoryPath + "/02.flac", Size = TrackSizeBytes };
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task PartialAlbumEnqueueRetainsSelectedManifestForBackgroundRecovery()
    {
        using var fixture = new TransferFixture();
        fixture.AcceptOnlyFirstTrack = true;
        await fixture.Helper.StartAlbumDownloadAsync(Source(), default);
        Assert.Single(fixture.Transfers);
        Assert.Equal(SecondTrack.Filename, Assert.Single(Source().MissingFiles(fixture.Downloads())).Filename);
    }

    [Fact]
    public async Task ExplicitSourceSwitchStillRejectsPartialEnqueue()
    {
        using var fixture = new TransferFixture();
        fixture.AcceptOnlyFirstTrack = true;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Helper.EnsureSelectedFilesEnqueuedAsync(Source(), default));
    }

    [Fact]
    public async Task EntirelyRejectedAlbumIsNotReportedAsQueued()
    {
        using var fixture = new TransferFixture();
        fixture.EnqueueStatus = HttpStatusCode.TooManyRequests;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Helper.StartAlbumDownloadAsync(Source(), default));
    }

    [Theory]
    [InlineData("Completed, Errored")]
    [InlineData("Completed, Cancelled")]
    [InlineData("Completed, Canceled")]
    [InlineData("Completed, TimedOut")]
    [InlineData("Completed, Rejected")]
    [InlineData("Completed, Aborted")]
    public async Task FailedTrackRetriesWithoutTouchingCompletedOrUnrelatedTransfers(string state)
    {
        using var fixture = new TransferFixture();
        fixture.Transfers.Add(Transfer(FirstTrack, "Completed, Succeeded"));
        fixture.Transfers.Add(Transfer(SecondTrack, state));
        fixture.Transfers.Add(Transfer(new SlskdFileDownloadRequest
            { Filename = "Music/AnotherAlbum/01.flac", Size = TrackSizeBytes }, "Completed, Errored"));
        Assert.True(await fixture.Helper.RetryMissingFileAsync(Source(), new SoulseekFileRetrySchedule(), default));
        Assert.Equal(SecondTrack.Filename, Assert.Single(Assert.Single(fixture.Submissions)).Filename);
        Assert.Equal("Completed, Succeeded", fixture.Transfers[0].State);
        Assert.Equal(0, fixture.DeleteRequests);
    }

    [Fact]
    public async Task MissingTrackIsSubmittedIndividually()
    {
        using var fixture = new TransferFixture();
        fixture.Transfers.Add(Transfer(FirstTrack, "Completed, Succeeded"));
        Assert.True(await fixture.Helper.RetryMissingFileAsync(Source(), new SoulseekFileRetrySchedule(), default));
        Assert.Equal(SecondTrack.Filename, Assert.Single(Assert.Single(fixture.Submissions)).Filename);
    }

    [Fact]
    public async Task AllFailedTracksRemainEligibleForIndividualRecovery()
    {
        using var fixture = new TransferFixture();
        fixture.Transfers.Add(Transfer(FirstTrack, "Completed, Rejected"));
        fixture.Transfers.Add(Transfer(SecondTrack, "Completed, TimedOut"));
        fixture.EnqueueStatus = HttpStatusCode.TooManyRequests;
        await fixture.Helper.StartAlbumDownloadAsync(Source(), default);
        fixture.Submissions.Clear();
        fixture.EnqueueStatus = HttpStatusCode.Created;
        Assert.Equal("Failed", SoulseekProvider.ReadProgress(Source(), fixture.Downloads()).State);
        Assert.True(await fixture.Helper.RetryMissingFileAsync(Source(), new SoulseekFileRetrySchedule(), default));
        Assert.Single(Assert.Single(fixture.Submissions));
    }

    [Fact]
    public async Task HealthyDuplicatePreventsRetryOfOlderFailure()
    {
        using var fixture = new TransferFixture();
        fixture.Transfers.Add(Transfer(FirstTrack, "Completed, Succeeded"));
        fixture.Transfers.Add(Transfer(SecondTrack, "Completed, Errored"));
        fixture.Transfers.Add(Transfer(SecondTrack, "Queued, Remotely"));
        Assert.False(await fixture.Helper.RetryMissingFileAsync(Source(), new SoulseekFileRetrySchedule(), default));
        Assert.Empty(fixture.Submissions);
    }

    [Fact]
    public async Task LegacySourceRetriesFailedTrackUsingItsFullPath()
    {
        using var fixture = new TransferFixture();
        var failed = Transfer(SecondTrack, "Completed, Errored");
        failed.Filename = "02.flac";
        fixture.Transfers.Add(Transfer(FirstTrack, "Completed, Succeeded"));
        fixture.Transfers.Add(failed);
        Assert.True(await fixture.Helper.RetryMissingFileAsync(Source() with { Files = [] },
            new SoulseekFileRetrySchedule(), default));
        Assert.Equal(SecondTrack.Filename, Assert.Single(Assert.Single(fixture.Submissions)).Filename);
    }

    [Fact]
    public async Task FailedTransferSnapshotDoesNotSubmitAnything()
    {
        using var fixture = new TransferFixture();
        fixture.SnapshotStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Helper.RetryMissingFileAsync(Source(), new SoulseekFileRetrySchedule(), default));
        Assert.Empty(fixture.Submissions);
    }

    [Fact]
    public async Task RateLimitedRetryKeepsCooldownAndDoesNotDeleteTransfers()
    {
        using var fixture = new TransferFixture();
        fixture.EnqueueStatus = HttpStatusCode.TooManyRequests;
        var schedule = new SoulseekFileRetrySchedule();
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Helper.RetryMissingFileAsync(Source(), schedule, default));
        Assert.True(await fixture.Helper.RetryMissingFileAsync(Source(), schedule, default));
        Assert.Single(fixture.Submissions);
        Assert.Equal(0, fixture.DeleteRequests);
        fixture.Clock.Now += SoulseekFileRetrySchedule.MinimumRetryDelay;
        fixture.EnqueueStatus = HttpStatusCode.Created;
        Assert.True(await fixture.Helper.RetryMissingFileAsync(Source(), schedule, default));
        Assert.Equal(2, fixture.Submissions.Count);
    }

    [Fact]
    public async Task RecoveryStopsOnceEverySelectedTrackCompletes()
    {
        using var fixture = new TransferFixture();
        fixture.Transfers.Add(Transfer(FirstTrack, "Completed, Succeeded"));
        fixture.Transfers.Add(Transfer(SecondTrack, "Completed, Succeeded"));
        Assert.False(await fixture.Helper.RetryMissingFileAsync(Source(), new SoulseekFileRetrySchedule(), default));
        Assert.Empty(fixture.Submissions);
        Assert.Equal("Completed", SoulseekProvider.ReadProgress(Source(), fixture.Downloads()).State);
    }

    [Fact]
    public async Task CancellationStopsRetrySubmission()
    {
        using var fixture = new TransferFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Helper.RetryMissingFileAsync(Source(), new SoulseekFileRetrySchedule(), cancellation.Token));
        Assert.Empty(fixture.Submissions);
    }

    [Fact]
    public void RetryDelayGrowsToCapAndContinuesWithoutAnAttemptLimit()
    {
        var schedule = new SoulseekFileRetrySchedule();
        var now = StartTime;
        var expectedDelay = SoulseekFileRetrySchedule.MinimumRetryDelay;
        const int RepeatedFailures = 100;
        for (var attempt = 0; attempt < RepeatedFailures; attempt++)
        {
            Assert.Same(FirstTrack, schedule.TakeNext([FirstTrack], now));
            Assert.Null(schedule.TakeNext([FirstTrack], now + expectedDelay - TimeSpan.FromTicks(1)));
            now += expectedDelay;
            expectedDelay = TimeSpan.FromTicks(Math.Min(expectedDelay.Ticks * 2,
                SoulseekFileRetrySchedule.MaximumRetryDelay.Ticks));
        }
    }

    [Fact]
    public void WaitingTrackDoesNotBlockAnotherEligibleTrack()
    {
        var schedule = new SoulseekFileRetrySchedule();
        Assert.Same(FirstTrack, schedule.TakeNext([FirstTrack, SecondTrack], StartTime));
        var next = StartTime + SoulseekFileRetrySchedule.MinimumRetryDelay;
        Assert.Same(FirstTrack, schedule.TakeNext([FirstTrack, SecondTrack], next));
        Assert.Same(SecondTrack, schedule.TakeNext([FirstTrack, SecondTrack],
            next + SoulseekFileRetrySchedule.MinimumRetryDelay));
    }

    [Theory]
    [InlineData("Completed, Errored")]
    [InlineData("Completed, Rejected")]
    [InlineData("Completed, TimedOut")]
    [InlineData("Completed, Aborted")]
    public void HalfCompletedAlbumCannotBeImportedAsComplete(string state)
    {
        using var fixture = new TransferFixture();
        fixture.Transfers.Add(Transfer(FirstTrack, "Completed, Succeeded"));
        fixture.Transfers.Add(Transfer(SecondTrack, state));
        var progress = SoulseekProvider.ReadProgress(Source(), fixture.Downloads());
        Assert.Equal(0.5, progress.Progress);
        Assert.Equal("Downloading", progress.State);
    }

    private static SlskdSourceIdentity Source() => new(Guid.NewGuid(), Username, DirectoryPath, [FirstTrack, SecondTrack]);

    private static SlskdTransferItem Transfer(SlskdFileDownloadRequest file, string state) => new()
        { Filename = file.Filename, Size = file.Size, State = state, PercentComplete = 100 };

    private sealed class TransferFixture : HttpMessageHandler, IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SlskdHelper Helper { get; }
        public ManualClock Clock { get; } = new();
        public List<SlskdTransferItem> Transfers { get; } = [];
        public List<List<SlskdFileDownloadRequest>> Submissions { get; } = [];
        public bool AcceptOnlyFirstTrack { get; set; }
        public HttpStatusCode EnqueueStatus { get; set; } = HttpStatusCode.Created;
        public HttpStatusCode SnapshotStatus { get; set; } = HttpStatusCode.OK;
        public int DeleteRequests { get; private set; }

        public TransferFixture()
        {
            _client = new HttpClient(this, disposeHandler: false);
            Helper = new SlskdHelper(this, Clock);
            Helper.Configure("http://slskd.test", "test-key");
        }

        public HttpClient CreateClient(string name) => _client;

        public List<SlskdTransferUser> Downloads() =>
            [new() { Username = Username, Directories = [new() { Directory = DirectoryPath, Files = Transfers }] }];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(SnapshotStatus)
                    { Content = new StringContent(JsonSerializer.Serialize(Downloads()), Encoding.UTF8, "application/json") };
            if (request.Method == HttpMethod.Delete)
            {
                DeleteRequests++;
                throw new InvalidOperationException("Retries must retain transfer records and completed files.");
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v0/transfers/downloads/" + Username, request.RequestUri!.AbsolutePath);
            var files = JsonSerializer.Deserialize<List<SlskdFileDownloadRequest>>(
                await request.Content!.ReadAsStringAsync(cancellationToken),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Submissions.Add(files);
            if (EnqueueStatus == HttpStatusCode.Created)
            {
                foreach (var file in AcceptOnlyFirstTrack ? files.Take(1) : files)
                {
                    Transfers.RemoveAll(transfer => transfer.Filename == file.Filename);
                    Transfers.Add(Transfer(file, "Queued, Locally"));
                }
            }
            return new HttpResponseMessage(EnqueueStatus);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _client.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = StartTime;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
