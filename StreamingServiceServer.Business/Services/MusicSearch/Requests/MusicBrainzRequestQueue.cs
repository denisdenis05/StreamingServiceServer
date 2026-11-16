using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StreamingServiceServer.Business.Services.MusicSearch.Requests;

public interface IMusicBrainzRequestQueue
{
    Task<HttpResponseMessage> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> sendRequest, CancellationToken cancellationToken);
}

public interface IMusicBrainzQueueClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class MusicBrainzQueueClock(TimeProvider timeProvider) : IMusicBrainzQueueClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, timeProvider, cancellationToken);
}

public sealed class MusicBrainzRequestQueue : IMusicBrainzRequestQueue, IAsyncDisposable
{
    public const int MaximumPendingRequests = 128;
    public const int MaximumAttempts = 4;
    public const string DefaultUserAgent = "StreamingService/1.0 (https://github.com/denisdenis05/StreamingServiceServer)";
    public static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(1100);
    public static readonly TimeSpan RetryDelayPerAttempt = TimeSpan.FromSeconds(2);
    private const int FirstAttempt = 1;
    private const string QueueCapacityMessage = "MusicBrainz request queue is full; retry later.";
    private const string QueueStoppedMessage = "MusicBrainz request queue has stopped.";
    private const string RetryLoopEndedMessage = "MusicBrainz retry loop ended without a response.";
    private const string TransportFailureTemplate = "MusicBrainz transport failed on attempt {Attempt}/{MaximumAttempts}.";
    private const string ServiceUnavailableTemplate = "MusicBrainz returned {StatusCode} on attempt {Attempt}/{MaximumAttempts}; shared cooldown {RetryDelay}.";
    public static IMusicBrainzRequestQueue Shared { get; } = new MusicBrainzRequestQueue(new MusicBrainzQueueClock(TimeProvider.System));

    private readonly Channel<RequestEntry> pendingRequests = Channel.CreateBounded<RequestEntry>(
        new BoundedChannelOptions(MaximumPendingRequests) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource stopping = new();
    private readonly IMusicBrainzQueueClock clock;
    private readonly ILogger<MusicBrainzRequestQueue> logger;
    private readonly Task worker;
    private DateTimeOffset nextRequestAt = DateTimeOffset.MinValue;

    public MusicBrainzRequestQueue(IMusicBrainzQueueClock clock, ILogger<MusicBrainzRequestQueue>? logger = null)
    {
        this.clock = clock;
        this.logger = logger ?? NullLogger<MusicBrainzRequestQueue>.Instance;
        worker = RunAsync();
    }

    public Task<HttpResponseMessage> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> sendRequest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new RequestEntry(sendRequest, cancellationToken, completion);
        if (!pendingRequests.Writer.TryWrite(entry))
            throw new HttpRequestException(stopping.IsCancellationRequested ? QueueStoppedMessage : QueueCapacityMessage);
        return AwaitCompletionAsync(completion, cancellationToken);
    }

    private static async Task<HttpResponseMessage> AwaitCompletionAsync(TaskCompletionSource<HttpResponseMessage> completion,
        CancellationToken cancellationToken)
    {
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var entry in pendingRequests.Reader.ReadAllAsync(stopping.Token))
            {
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(entry.CancellationToken, stopping.Token);
                try
                {
                    var response = await ProcessAsync(entry.SendRequest, operation.Token);
                    if (operation.IsCancellationRequested)
                    {
                        response.Dispose();
                        operation.Token.ThrowIfCancellationRequested();
                    }
                    if (!entry.Completion.TrySetResult(response)) response.Dispose();
                }
                catch (OperationCanceledException) { entry.Completion.TrySetCanceled(operation.Token); }
                catch (Exception exception) { entry.Completion.TrySetException(exception); }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally
        {
            while (pendingRequests.Reader.TryRead(out var entry)) entry.Completion.TrySetCanceled(stopping.Token);
        }
    }

    private async Task<HttpResponseMessage> ProcessAsync(Func<CancellationToken, Task<HttpResponseMessage>> sendRequest, CancellationToken cancellationToken)
    {
        for (var attempt = FirstAttempt; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delay = nextRequestAt - clock.UtcNow;
            if (delay > TimeSpan.Zero) await clock.DelayAsync(delay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            nextRequestAt = clock.UtcNow + MinimumRequestInterval;
            HttpResponseMessage response;
            try { response = await sendRequest(cancellationToken); }
            catch (Exception exception) when (IsRetryableTransportFailure(exception, cancellationToken) && attempt < MaximumAttempts)
            {
                ApplyCooldown(RetryDelayPerAttempt * attempt);
                logger.LogWarning(exception, TransportFailureTemplate, attempt, MaximumAttempts);
                continue;
            }
            if (response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)) return response;
            var retryDelay = GetRetryDelay(response, attempt);
            ApplyCooldown(retryDelay);
            logger.LogWarning(ServiceUnavailableTemplate,
                response.StatusCode, attempt, MaximumAttempts, retryDelay);
            if (attempt == MaximumAttempts)
            {
                using (response) response.EnsureSuccessStatusCode();
            }
            response.Dispose();
        }
        throw new InvalidOperationException(RetryLoopEndedMessage);
    }

    private static bool IsRetryableTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is HttpRequestException or IOException or OperationCanceledException;

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var backoff = RetryDelayPerAttempt * attempt;
        var retryAfter = response.Headers.RetryAfter;
        var requestedDelay = retryAfter?.Delta ?? (retryAfter?.Date - clock.UtcNow) ?? TimeSpan.Zero;
        return requestedDelay > backoff ? requestedDelay : backoff;
    }

    private void ApplyCooldown(TimeSpan delay)
    {
        var cooldownEndsAt = clock.UtcNow + delay;
        if (cooldownEndsAt > nextRequestAt) nextRequestAt = cooldownEndsAt;
    }

    public async ValueTask DisposeAsync()
    {
        pendingRequests.Writer.TryComplete();
        await stopping.CancelAsync();
        await worker;
        stopping.Dispose();
    }

    private sealed record RequestEntry(Func<CancellationToken, Task<HttpResponseMessage>> SendRequest,
        CancellationToken CancellationToken, TaskCompletionSource<HttpResponseMessage> Completion);
}
