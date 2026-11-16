using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Business.Services.MusicSearch.Requests;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class MusicBrainzRequestQueueTests
{
    private const int NoAttempts = 0;
    private const int FirstAttempt = 1;
    private const int SecondAttempt = 2;
    private const string FirstRequest = "first";
    private const string SecondRequest = "second";
    private const string ThirdRequest = "third";
    private const string TransportFailure = "Temporary network failure";
    private const string EmptyMetadata = "{}";
    private const string MetadataAddress = "https://metadata.invalid/ws/2/";
    private const string MetadataConfigurationKey = "Music:External:MusicBrainz:BaseUrl";
    private const string ArtistSearchQuery = "queue test artist";
    private static readonly TimeSpan RequestedCooldown = TimeSpan.FromSeconds(10);
    private const string InitialTimestamp = "2026-11-16T00:00:00+00:00";
    private const string CoverAddressTemplate = "https://coverartarchive.org/release/{0}/front";
    private static readonly DateTimeOffset InitialTime = DateTimeOffset.Parse(InitialTimestamp, System.Globalization.CultureInfo.InvariantCulture);
    private static readonly Guid ReleaseIdentifier = Guid.Parse("13d0cfca-9886-47ba-8e1f-b5268f00350c");

    [Fact]
    public async Task ConcurrentRequestsRunInFifoOrderWithoutOverlapAndWithGlobalSpacing()
    {
        var clock = new AdvancingClock();
        await using var queue = new MusicBrainzRequestQueue(clock);
        var firstStarted = Completion();
        var releaseFirst = Completion();
        var requests = new List<string>();
        var startedAt = new List<DateTimeOffset>();
        var first = queue.SendAsync(async cancellationToken =>
        {
            requests.Add(FirstRequest);
            startedAt.Add(clock.UtcNow);
            firstStarted.SetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            return Success();
        }, CancellationToken.None);
        await firstStarted.Task;
        var second = queue.SendAsync(cancellationToken =>
        {
            requests.Add(SecondRequest);
            startedAt.Add(clock.UtcNow);
            return Task.FromResult(Success());
        }, CancellationToken.None);
        var third = queue.SendAsync(cancellationToken =>
        {
            requests.Add(ThirdRequest);
            startedAt.Add(clock.UtcNow);
            return Task.FromResult(Success());
        }, CancellationToken.None);
        Assert.Equal(new[] { FirstRequest }, requests);
        releaseFirst.SetResult();
        foreach (var response in await Task.WhenAll(first, second, third)) response.Dispose();
        Assert.Equal(new[] { FirstRequest, SecondRequest, ThirdRequest }, requests);
        Assert.Equal(new[] { InitialTime, InitialTime + MusicBrainzRequestQueue.MinimumRequestInterval,
            InitialTime + MusicBrainzRequestQueue.MinimumRequestInterval * SecondAttempt }, startedAt);
    }

    [Fact]
    public async Task RetriesHonorRetryAfterAndCooldownAlsoDelaysOtherCallers()
    {
        var clock = new AdvancingClock();
        await using var queue = new MusicBrainzRequestQueue(clock);
        var startedAt = new List<DateTimeOffset>();
        var attempts = NoAttempts;
        using var response = await queue.SendAsync(cancellationToken =>
        {
            startedAt.Add(clock.UtcNow);
            attempts++;
            if (attempts == FirstAttempt)
            {
                var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(RequestedCooldown);
                return Task.FromResult(unavailable);
            }
            return Task.FromResult(attempts == SecondAttempt
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Success());
        }, CancellationToken.None);
        using var followingResponse = await queue.SendAsync(cancellationToken =>
        {
            startedAt.Add(clock.UtcNow);
            return Task.FromResult(Success());
        }, CancellationToken.None);
        var secondRetryAt = InitialTime + RequestedCooldown + MusicBrainzRequestQueue.RetryDelayPerAttempt * SecondAttempt;
        Assert.Equal(new[] { InitialTime, InitialTime + RequestedCooldown, secondRetryAt,
            secondRetryAt + MusicBrainzRequestQueue.MinimumRequestInterval }, startedAt);
    }

    [Fact]
    public async Task HttpDateRetryAfterPausesTheWholeQueueAfterFinalFailure()
    {
        var clock = new AdvancingClock();
        await using var queue = new MusicBrainzRequestQueue(clock);
        var finalAttemptTime = InitialTime;
        await Assert.ThrowsAsync<HttpRequestException>(() => queue.SendAsync(cancellationToken =>
        {
            finalAttemptTime = clock.UtcNow;
            var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(clock.UtcNow + RequestedCooldown);
            return Task.FromResult(unavailable);
        }, CancellationToken.None));
        using var response = await queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None);
        Assert.Equal(finalAttemptTime + RequestedCooldown, clock.UtcNow);
    }

    [Fact]
    public async Task TransportFailuresRetryWithinTheSameQueueAndPreserveTheAttemptBound()
    {
        var clock = new AdvancingClock();
        await using var queue = new MusicBrainzRequestQueue(clock);
        var attempts = NoAttempts;
        await Assert.ThrowsAsync<HttpRequestException>(() => queue.SendAsync(cancellationToken =>
        {
            attempts++;
            throw new HttpRequestException(TransportFailure);
        }, CancellationToken.None));
        Assert.Equal(MusicBrainzRequestQueue.MaximumAttempts, attempts);
        Assert.All(clock.Delays, delay => Assert.True(delay >= MusicBrainzRequestQueue.MinimumRequestInterval));
        using var response = await queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CancelledQueuedRequestsNeverReachTransportAndDoNotBlockFollowingRequests()
    {
        var clock = new AdvancingClock();
        await using var queue = new MusicBrainzRequestQueue(clock);
        var firstStarted = Completion();
        var releaseFirst = Completion();
        var first = queue.SendAsync(async cancellationToken =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            return Success();
        }, CancellationToken.None);
        await firstStarted.Task;
        using var cancellation = new CancellationTokenSource();
        var transportCalled = false;
        var cancelled = queue.SendAsync(cancellationToken =>
        {
            transportCalled = true;
            return Task.FromResult(Success());
        }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var following = queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None);
        releaseFirst.SetResult();
        using var firstResponse = await first;
        using var followingResponse = await following;
        Assert.False(transportCalled);
    }

    [Fact]
    public async Task CancellingAnActiveRequestCancelsTransportWithoutRetrying()
    {
        var clock = new AdvancingClock();
        await using var queue = new MusicBrainzRequestQueue(clock);
        var started = Completion();
        using var cancellation = new CancellationTokenSource();
        var attempts = NoAttempts;
        var request = queue.SendAsync(async cancellationToken =>
        {
            attempts++;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Success();
        }, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        using var followingResponse = await queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None);
        Assert.Equal(FirstAttempt, attempts);
    }

    [Fact]
    public async Task CancellationDuringPacingDelaySkipsTransportAndPreservesTheNextSlot()
    {
        var clock = new PausingClock();
        await using var queue = new MusicBrainzRequestQueue(clock);
        using var firstResponse = await queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var transportCalled = false;
        var waiting = queue.SendAsync(cancellationToken =>
        {
            transportCalled = true;
            return Task.FromResult(Success());
        }, cancellation.Token);
        await clock.DelayEntered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        using var followingResponse = await queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None);
        Assert.False(transportCalled);
        Assert.Equal(InitialTime + MusicBrainzRequestQueue.MinimumRequestInterval, clock.UtcNow);
    }

    [Fact]
    public async Task TimeoutRetriesButAnUnsuccessfulFinalResponseIsDisposed()
    {
        await using var queue = new MusicBrainzRequestQueue(new AdvancingClock());
        var attempts = NoAttempts;
        var responseContents = new List<TrackedContent>();
        await Assert.ThrowsAsync<HttpRequestException>(() => queue.SendAsync(cancellationToken =>
        {
            attempts++;
            if (attempts == FirstAttempt) throw new TaskCanceledException(TransportFailure);
            var content = new TrackedContent();
            responseContents.Add(content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content });
        }, CancellationToken.None));
        Assert.Equal(MusicBrainzRequestQueue.MaximumAttempts, attempts);
        Assert.All(responseContents, content => Assert.True(content.IsDisposed));
    }

    [Fact]
    public async Task SuccessfulResponsesRemainOwnedByTheCallerAndNotFoundIsNotRetried()
    {
        await using var queue = new MusicBrainzRequestQueue(new AdvancingClock());
        var content = new TrackedContent();
        var attempts = NoAttempts;
        var response = await queue.SendAsync(cancellationToken =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = content });
        }, CancellationToken.None);
        Assert.Equal(FirstAttempt, attempts);
        Assert.False(content.IsDisposed);
        response.Dispose();
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public async Task CancellationDisposesALateTransportResponseWithoutAffectingTheNextCaller()
    {
        await using var queue = new MusicBrainzRequestQueue(new AdvancingClock());
        var started = Completion();
        var releaseResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var cancelled = queue.SendAsync(cancellationToken =>
        {
            started.SetResult();
            return releaseResponse.Task;
        }, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var following = queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None);
        var content = new TrackedContent();
        releaseResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var followingResponse = await following;
        Assert.True(content.IsDisposed);
        Assert.Equal(HttpStatusCode.OK, followingResponse.StatusCode);
    }

    [Fact]
    public async Task UnexpectedTransportFailureDoesNotKillTheQueueOrRetryInvalidWork()
    {
        await using var queue = new MusicBrainzRequestQueue(new AdvancingClock());
        var attempts = NoAttempts;
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.SendAsync(cancellationToken =>
        {
            attempts++;
            throw new InvalidOperationException(TransportFailure);
        }, CancellationToken.None));
        using var followingResponse = await queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None);
        Assert.Equal(FirstAttempt, attempts);
        Assert.Equal(HttpStatusCode.OK, followingResponse.StatusCode);
    }

    [Fact]
    public async Task PreCancelledRequestsAreRejectedWithoutInvokingTransport()
    {
        await using var queue = new MusicBrainzRequestQueue(new AdvancingClock());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transportCalled = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.SendAsync(cancellationToken =>
        {
            transportCalled = true;
            return Task.FromResult(Success());
        }, cancellation.Token));
        Assert.False(transportCalled);
    }

    [Fact]
    public async Task QueueRejectsOverflowInsteadOfAccumulatingUnboundedWaiters()
    {
        var clock = new AdvancingClock();
        await using var queue = new MusicBrainzRequestQueue(clock);
        var started = Completion();
        var releaseFirst = Completion();
        var first = queue.SendAsync(async cancellationToken =>
        {
            started.SetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            return Success();
        }, CancellationToken.None);
        await started.Task;
        var pending = Enumerable.Range(FirstAttempt, MusicBrainzRequestQueue.MaximumPendingRequests)
            .Select(requestNumber => queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None)).ToArray();
        await Assert.ThrowsAsync<HttpRequestException>(() => queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None));
        releaseFirst.SetResult();
        using var firstResponse = await first;
        foreach (var response in await Task.WhenAll(pending)) response.Dispose();
    }

    [Fact]
    public async Task StoppingQueueCancelsActiveAndPendingRequestsAndRejectsNewWork()
    {
        var queue = new MusicBrainzRequestQueue(new AdvancingClock());
        var started = Completion();
        var active = queue.SendAsync(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Success();
        }, CancellationToken.None);
        await started.Task;
        var pending = queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None);
        await queue.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<HttpRequestException>(() => queue.SendAsync(cancellationToken => Task.FromResult(Success()), CancellationToken.None));
    }

    [Fact]
    public async Task MusicMetadataAndCoverMetadataUseTheSameQueue()
    {
        var clock = new AdvancingClock();
        await using var queue = new MusicBrainzRequestQueue(clock);
        using var handler = new MetadataHandler(clock);
        using var client = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [MetadataConfigurationKey] = MetadataAddress
        }).Build();
        var service = new MusicBrainzService(client, configuration, queue);
        var fallback = new CoverArchiveFallback(() => new HttpClient(handler, false), new Uri(MetadataAddress), TimeProvider.System, queue);
        await service.SearchArtistsAsync(ArtistSearchQuery);
        await fallback.ResolveAsync(new Uri(string.Format(System.Globalization.CultureInfo.InvariantCulture, CoverAddressTemplate, ReleaseIdentifier)), CancellationToken.None);
        Assert.Equal(new[] { InitialTime, InitialTime + MusicBrainzRequestQueue.MinimumRequestInterval }, handler.StartedAt);
    }

    [Fact]
    public async Task MetadataServicePropagatesExhausted503InsteadOfReturningMissingMetadata()
    {
        await using var queue = new MusicBrainzRequestQueue(new AdvancingClock());
        using var handler = new UnavailableHandler();
        using var client = new HttpClient(handler);
        var service = new MusicBrainzService(client, new ConfigurationBuilder().Build(), queue);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchArtistsAsync(ArtistSearchQuery));
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK);
    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class AdvancingClock : IMusicBrainzQueueClock
    {
        public DateTimeOffset UtcNow { get; private set; } = InitialTime;
        public List<TimeSpan> Delays { get; } = new();
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class PausingClock : IMusicBrainzQueueClock
    {
        private bool pauseNextDelay = true;
        public DateTimeOffset UtcNow { get; private set; } = InitialTime;
        public TaskCompletionSource DelayEntered { get; } = Completion();
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (pauseNextDelay)
            {
                pauseNextDelay = false;
                DelayEntered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
        }
    }

    private sealed class TrackedContent : ByteArrayContent
    {
        public TrackedContent() : base(Array.Empty<byte>()) { }
        public bool IsDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class MetadataHandler(AdvancingClock clock) : HttpMessageHandler
    {
        public List<DateTimeOffset> StartedAt { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            StartedAt.Add(clock.UtcNow);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EmptyMetadata) });
        }
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
