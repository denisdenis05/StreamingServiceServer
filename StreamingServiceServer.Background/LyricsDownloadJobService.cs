using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Lyrics;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Background;

public sealed class LyricsDownloadJobService(IServiceScopeFactory scopes, IOptions<LyricsOptions> configuration,
    TimeProvider clock, ILogger<LyricsDownloadJobService> logger) : BackgroundService
{
    private const string ProcessingFailureMessage = "Lyrics downloads could not complete their current pass.";
    private DateTimeOffset nextDiscoveryAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim processing = new(LyricsProtocol.SingleWorker, LyricsProtocol.SingleWorker);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning(ProcessingFailureMessage); }
            await Task.Delay(configuration.Value.PollInterval, clock, stoppingToken);
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        if (!configuration.Value.Enabled) return false;
        await processing.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ILyricsJobStore>();
            if (clock.GetUtcNow() >= nextDiscoveryAt)
            {
                await store.DiscoverAsync(configuration.Value.DiscoveryBatchSize, cancellationToken);
                nextDiscoveryAt = clock.GetUtcNow() + configuration.Value.DiscoveryInterval;
            }
            var lease = await store.ClaimAsync(configuration.Value.LeaseLifetime, cancellationToken);
            if (lease is null) return false;
            var nextRequestAt = clock.GetUtcNow() + configuration.Value.RequestInterval;
            using var deadline = new CancellationTokenSource(configuration.Value.OperationTimeout, clock);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                var catalog = scope.ServiceProvider.GetRequiredService<ILyricsCatalog>();
                var track = await catalog.ReadAsync(lease.Job.RecordingIdentifier, operation.Token);
                if (track is null) return true;
                var rejection = LyricsMatchPolicy.Rejection(track);
                if (rejection is not null)
                {
                    await store.CompleteAsync(lease, track, LyricsDownloadState.Skipped, null, rejection,
                        clock.GetUtcNow() + configuration.Value.MetadataRecheckInterval, operation.Token);
                    return true;
                }
                if (lease.Job.SourceRevision == track.Revision
                    && lease.Job.State is LyricsDownloadState.Downloaded or LyricsDownloadState.Instrumental)
                {
                    await store.CompleteAsync(lease, track, lease.Job.State,
                        new LyricsContent(lease.Job.ProviderIdentifier!.Value, lease.Job.PlainLyrics, lease.Job.SyncedLyrics,
                            lease.Job.Lyricsfile, lease.Job.State == LyricsDownloadState.Instrumental), string.Empty,
                        clock.GetUtcNow() + configuration.Value.MetadataRecheckInterval, operation.Token);
                    return true;
                }
                var provider = scope.ServiceProvider.GetRequiredService<ILyricsProvider>();
                var content = await provider.DownloadAsync(track, operation.Token);
                var current = await catalog.ReadAsync(track.RecordingIdentifier, operation.Token);
                if (current?.Revision != track.Revision)
                {
                    nextRequestAt = clock.GetUtcNow() + configuration.Value.RequestInterval;
                    await store.FailAsync(lease, LyricsProtocol.ChangedMetadata, clock.GetUtcNow(), nextRequestAt, operation.Token);
                    return true;
                }
                var state = content is null ? LyricsDownloadState.NotFound
                    : content.Instrumental ? LyricsDownloadState.Instrumental : LyricsDownloadState.Downloaded;
                await store.CompleteAsync(lease, track, state, content, content is null ? LyricsProtocol.MissingLyrics : string.Empty,
                    clock.GetUtcNow() + (content is null ? configuration.Value.NotFoundRetryInterval
                        : configuration.Value.MetadataRecheckInterval), operation.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (LyricsProviderException failure) when (failure.FailureCode is LyricsProtocol.UnmatchedRecording or LyricsProtocol.AmbiguousRecording)
            {
                var track = await scope.ServiceProvider.GetRequiredService<ILyricsCatalog>().ReadAsync(lease.Job.RecordingIdentifier, cancellationToken);
                if (track is not null) await store.CompleteAsync(lease, track, LyricsDownloadState.Skipped, null, failure.FailureCode,
                    clock.GetUtcNow() + configuration.Value.MetadataRecheckInterval, cancellationToken);
            }
            catch (Exception failure)
            {
                var delay = RetryDelay(lease.Job.Attempts, configuration.Value);
                if (failure is LyricsProviderException { RetryAfter: { } retryAfter } && retryAfter > delay) delay = retryAfter;
                var providerDelay = failure is LyricsProviderException { RetryAfter: { } cooldown }
                    ? cooldown : failure is HttpRequestException ? configuration.Value.InitialRetryDelay : configuration.Value.RequestInterval;
                if (providerDelay < configuration.Value.RequestInterval) providerDelay = configuration.Value.RequestInterval;
                nextRequestAt = clock.GetUtcNow() + providerDelay;
                var code = failure is LyricsProviderException providerFailure ? providerFailure.FailureCode
                    : deadline.IsCancellationRequested ? LyricsProtocol.ProviderTimeout : LyricsProtocol.ProviderFailure;
                await store.FailAsync(lease, code, clock.GetUtcNow() + delay, nextRequestAt, cancellationToken);
            }
            finally
            {
                var minimumNextRequest = clock.GetUtcNow() + configuration.Value.RequestInterval;
                await store.ReleaseAsync(lease, nextRequestAt > minimumNextRequest ? nextRequestAt : minimumNextRequest, CancellationToken.None);
            }
            return true;
        }
        finally { processing.Release(); }
    }

    public static TimeSpan RetryDelay(int previousAttempts, LyricsOptions options)
    {
        var exponent = Math.Clamp(previousAttempts, LyricsProtocol.FirstRetryExponent, LyricsProtocol.MaximumRetryExponent);
        var ticks = Math.Min(options.MaximumRetryDelay.Ticks,
            options.InitialRetryDelay.Ticks * Math.Pow(LyricsProtocol.RetryMultiplier, exponent));
        return TimeSpan.FromTicks((long)ticks);
    }
}
